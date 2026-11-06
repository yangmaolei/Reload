using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Paths;

namespace GameSaveSync.Core.Locator;

/// <summary>一条存档路径规则。<see cref="Roots"/> 里的模板支持 <c>{name}</c> / <c>{installDir}</c> / <c>{installDirName}</c>。</summary>
public sealed class SavePathRule
{
    public required string Id { get; init; }

    public string? Description { get; init; }

    /// <summary>按游戏名匹配的通配符，默认 <c>*</c>。</summary>
    public string NamePattern { get; init; } = "*";

    /// <summary>限定引擎（unity / unreal / rpgmaker / renpy / gamemaker），空表示不限。</summary>
    public GameEngine? Engine { get; init; }

    public List<string> Roots { get; init; } = new();
}

/// <summary>
/// 规则库：内置一份，外加数据目录下的 <c>save-paths.yaml</c> 覆盖（同 id 替换、新 id 追加）。
/// 这样"加一条规则"不需要改代码。
/// </summary>
public sealed class SavePathRuleSet
{
    public const string OverrideFileName = "save-paths.yaml";

    public const string SampleFileName = "save-paths.sample.yaml";

    private static readonly Lazy<SavePathRuleSet> LazyBuiltIn = new(() => Parse(BuiltInYaml));

    public SavePathRuleSet(int version, IReadOnlyList<SavePathRule> rules)
    {
        Version = version;
        Rules = rules;
    }

    public int Version { get; }

    public IReadOnlyList<SavePathRule> Rules { get; }

    public static SavePathRuleSet BuiltIn => LazyBuiltIn.Value;

    public static SavePathRuleSet Parse(string yaml)
    {
        var root = MiniYaml.ParseMapping(yaml);
        var version = 1;
        if (root is not null && root.TryGetValue("version", out var versionValue) &&
            versionValue is string versionText && int.TryParse(versionText, out var parsedVersion))
        {
            version = parsedVersion;
        }

        var rules = new List<SavePathRule>();
        if (root is null || !root.TryGetValue("rules", out var rulesValue) || rulesValue is not List<object?> items)
        {
            return new SavePathRuleSet(version, rules);
        }

        foreach (var item in items.OfType<IReadOnlyDictionary<string, object?>>())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            GameEngine? engine = null;
            var engineText = GetString(item, "engine");
            if (!string.IsNullOrWhiteSpace(engineText) &&
                Enum.TryParse<GameEngine>(engineText, ignoreCase: true, out var parsedEngine))
            {
                engine = parsedEngine;
            }

            var roots = new List<string>();
            if (item.TryGetValue("roots", out var rootsValue) && rootsValue is List<object?> rootItems)
            {
                roots.AddRange(rootItems.OfType<string>());
            }

            rules.Add(new SavePathRule
            {
                Id = id,
                Description = GetString(item, "description"),
                NamePattern = GetString(item, "namePattern") ?? "*",
                Engine = engine,
                Roots = roots,
            });
        }

        return new SavePathRuleSet(version, rules);
    }

    /// <summary>首次启动在数据目录放一份带注释的样例，用户复制成 save-paths.yaml 即可生效。</summary>
    public static void WriteSampleIfMissing(IFileSystem fileSystem, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var path = Path.Combine(dataRoot, SampleFileName);
        if (fileSystem.FileExists(path))
        {
            return;
        }

        fileSystem.CreateDirectory(dataRoot);
        using var stream = fileSystem.OpenCreate(path);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(SampleYaml);
    }

    /// <summary>读取外部覆盖并合并到内置规则；文件不存在或解析失败时退回内置。</summary>
    public static SavePathRuleSet Load(string overridePath, IFileSystem fileSystem, out string? warning)
    {
        warning = null;
        if (!fileSystem.FileExists(overridePath))
        {
            return BuiltIn;
        }

        try
        {
            using var stream = fileSystem.OpenRead(overridePath);
            using var reader = new StreamReader(stream);
            var external = Parse(reader.ReadToEnd());
            if (external.Rules.Count == 0)
            {
                warning = "外部规则文件里没有有效规则，已忽略";
                return BuiltIn;
            }

            var merged = new List<SavePathRule>();
            var externalById = external.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var rule in BuiltIn.Rules)
            {
                merged.Add(externalById.TryGetValue(rule.Id, out var replacement) ? replacement : rule);
            }

            foreach (var rule in external.Rules)
            {
                if (!merged.Any(r => r.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(rule);
                }
            }

            return new SavePathRuleSet(Math.Max(external.Version, BuiltIn.Version), merged);
        }
        catch (Exception ex)
        {
            warning = $"解析外部规则失败（{ex.Message}），已使用内置规则";
            return BuiltIn;
        }
    }

    public IEnumerable<SavePathRule> Match(DiscoveredGame game)
    {
        ArgumentNullException.ThrowIfNull(game);

        foreach (var rule in Rules)
        {
            if (rule.Engine is { } engine && engine != game.Engine)
            {
                continue;
            }

            if (!GlobMatcher.IsMatchPattern(rule.NamePattern, game.Name))
            {
                continue;
            }

            yield return rule;
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>给用户抄的样例：同 id 会覆盖内置规则，新 id 追加。</summary>
    public const string SampleYaml = """
        # Reload 自定义存档路径规则
        # 用法：把本文件改名为 save-paths.yaml 放在数据目录（设置页有完整路径），重启后生效。
        # 规则按游戏名通配符匹配，roots 支持 %DOCUMENTS% %USERPROFILE% %APPDATA% %LOCALAPPDATA% %SAVEDGAMES%
        # 以及 {name} {installDir} {installDirName} 占位符。目录必须真实存在且包含存档类文件才会被采用。

        version: 1
        rules:
          # 例 1：某发行商把存档放在 Documents\MyCompany\<游戏名>
          - id: my-company
            description: MyCompany 的固定布局
            namePattern: "*"
            roots:
              - %DOCUMENTS%\MyCompany\{name}

          # 例 2：只对名字以 "Old" 开头的游戏生效，覆盖安装目录下的 saves
          - id: old-games
            description: 老游戏把存档放在安装目录
            namePattern: "Old*"
            roots:
              - {installDir}\saves
              - {installDir}\data
        """;

    /// <summary>
    /// 内置规则：只写"结构稳定、跨平台通用"的模板。装了什么引擎、存档落在哪，
    /// 由 <see cref="SavePathResolver"/> 结合引擎识别做最后一层过滤（目录必须真实存在且有存档文件）。
    /// </summary>
    public const string BuiltInYaml = """
        version: 1
        rules:
          - id: my-games
            description: Documents\My Games\<game>
            namePattern: "*"
            roots:
              - %DOCUMENTS%\My Games\{name}
              - %DOCUMENTS%\My Games\{name}\Saves
              - %DOCUMENTS%\My Games\{name}\SaveGames
          - id: documents-game
            description: Documents\<game>
            namePattern: "*"
            roots:
              - %DOCUMENTS%\{name}
              - %DOCUMENTS%\{name}\Saves
              - %DOCUMENTS%\{name}\Savegames
              - %DOCUMENTS%\{name}\save
          - id: saved-games
            description: Saved Games\<game>
            namePattern: "*"
            roots:
              - %SAVEDGAMES%\{name}
              - %SAVEDGAMES%\{name}\Saves
          - id: roaming-appdata
            description: %APPDATA%\<game>
            namePattern: "*"
            roots:
              - %APPDATA%\{name}
              - %APPDATA%\{name}\Saves
              - %APPDATA%\{name}\save
          - id: local-appdata
            description: %LOCALAPPDATA%\<game>
            namePattern: "*"
            roots:
              - %LOCALAPPDATA%\{name}
              - %LOCALAPPDATA%\{name}\Saves
              - %LOCALAPPDATA%\{name}\save
              - %LOCALAPPDATA%\{name}\Saved\SaveGames
          - id: unity-local-low
            description: Unity 默认 LocalLow\<Company>\<Product>
            engine: unity
            namePattern: "*"
            roots: []
          - id: unreal-saved
            description: Unreal Saved\SaveGames
            engine: unreal
            namePattern: "*"
            roots:
              - %LOCALAPPDATA%\{name}\Saved\SaveGames
              - {installDir}\{name}\Saved\SaveGames
              - {installDir}\Saved\SaveGames
          - id: install-save
            description: 游戏安装目录下的 save / saves
            namePattern: "*"
            roots:
              - {installDir}\save
              - {installDir}\saves
              - {installDir}\SaveData
              - {installDir}\SaveGames
              - {installDir}\userdata
              - {installDir}\data\save
          - id: rpgmaker
            description: RPG Maker MV/MZ
            engine: rpgmaker
            namePattern: "*"
            roots:
              - {installDir}\www\save
              - {installDir}\save
              - %SAVEDGAMES%\{name}
          - id: renpy
            description: Ren'Py
            engine: renpy
            namePattern: "*"
            roots:
              - {installDir}\game\saves
              - %APPDATA%\RenPy\{name}
          - id: gamemaker
            description: GameMaker
            engine: gamemaker
            namePattern: "*"
            roots:
              - %LOCALAPPDATA%\{name}
              - {installDir}\save
        """;
}

/// <summary>规则模板展开 + 引擎雷达 + "这个目录像不像存档目录"的最后判定。</summary>
public sealed class SavePathResolver
{
    private static readonly string[] SaveExtensions =
    {
        ".sav", ".save", ".savx", ".dat", ".slot", ".srm", ".sgm", ".rpgsave", ".rxdata", ".rvdata2",
        ".lsd", ".es3", ".profile", ".user", ".sga", ".ugc", ".ss1", ".gam", ".json", ".xml",
    };

    private static readonly string[] SaveNameHints =
    {
        "save", "saves", "savedata", "savegame", "savegames", "profile", "slot", "userdata",
        "persistent", "progress", "checkpoint", "autosave",
    };

    private readonly IFileSystem _fileSystem;
    private readonly IPathTokenResolver _tokens;
    private readonly SavePathRuleSet _rules;

    public SavePathResolver(IFileSystem fileSystem, IPathTokenResolver tokens, SavePathRuleSet? rules = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _rules = rules ?? SavePathRuleSet.BuiltIn;
    }

    public IReadOnlyList<string> ResolveRoots(DiscoveredGame game)
    {
        ArgumentNullException.ThrowIfNull(game);

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, bool trusted)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = _fileSystem.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return;
            }

            if (!seen.Add(full))
            {
                return;
            }

            if (IsPlausible(full, trusted))
            {
                results.Add(full);
            }
        }

        // 平台定位器已经明确指出的目录（Steam userdata / Ubisoft / UWP）：直接信
        foreach (var root in game.SuggestedRoots)
        {
            Add(root, trusted: true);
        }

        var installDirName = string.IsNullOrWhiteSpace(game.InstallDir)
            ? null
            : Path.GetFileName(game.InstallDir.TrimEnd('\\', '/'));

        foreach (var rule in _rules.Match(game))
        {
            foreach (var template in rule.Roots)
            {
                foreach (var name in BuildNameVariants(game.Name, installDirName))
                {
                    var expanded = ExpandTemplate(template, name, game.InstallDir, installDirName);
                    if (expanded is null)
                    {
                        continue;
                    }

                    Add(expanded, trusted: false);
                }
            }
        }

        if (game.Engine == GameEngine.Unity)
        {
            foreach (var candidate in ScanUnityLocalLow(game, installDirName))
            {
                Add(candidate, trusted: false);
            }
        }

        return results;
    }

    /// <summary>把规则模板变成绝对路径；令牌展开失败返回 null（不猜路径）。</summary>
    private string? ExpandTemplate(string template, string name, string? installDir, string? installDirName)
    {
        var expanded = template
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{installDirName}", installDirName ?? name, StringComparison.OrdinalIgnoreCase)
            .Replace("{installDir}", installDir ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (expanded.Contains('{', StringComparison.Ordinal))
        {
            return null;
        }

        if (!expanded.Contains('%', StringComparison.Ordinal))
        {
            return expanded;
        }

        return _tokens.TryExpand(expanded, out var absolute, out var missing) && missing is null
            ? absolute
            : null;
    }

    private IEnumerable<string> ScanUnityLocalLow(DiscoveredGame game, string? installDirName)
    {
        string localLow;
        try
        {
            localLow = Path.Combine(_tokens.Tokens["%USERPROFILE%"], "AppData", "LocalLow");
        }
        catch
        {
            yield break;
        }

        if (!_fileSystem.DirectoryExists(localLow))
        {
            yield break;
        }

        var variants = BuildNameVariants(game.Name, installDirName)
            .Select(Normalize)
            .Where(v => v.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> companies;
        try
        {
            companies = _fileSystem.EnumerateDirectories(localLow, recursive: false);
        }
        catch
        {
            yield break;
        }

        foreach (var company in companies)
        {
            IEnumerable<string> products;
            try
            {
                products = _fileSystem.EnumerateDirectories(company, recursive: false);
            }
            catch
            {
                continue;
            }

            foreach (var product in products)
            {
                var productName = Normalize(Path.GetFileName(product));
                var companyName = Normalize(Path.GetFileName(company));

                var hit = variants.Any(v =>
                    productName.Contains(v, StringComparison.OrdinalIgnoreCase) ||
                    v.Contains(productName, StringComparison.OrdinalIgnoreCase) || productName == v);

                if (hit || variants.Contains(companyName))
                {
                    yield return product;
                }
            }
        }
    }

    private bool IsPlausible(string directory, bool trusted)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(directory))
            {
                return false;
            }

            // 平台定位器明确指出的存档目录（Steam userdata / Ubisoft / Xbox wgs）直接采信：
            // 目录已存在就说明位置正确，哪怕游戏还没来得及写盘（目录暂时为空）。
            if (trusted)
            {
                return true;
            }

            var files = _fileSystem.EnumerateFiles(directory, recursive: true);
            if (files.Count == 0)
            {
                return false;
            }

            var sample = files.Count > 400 ? files.Take(400) : files;
            var hasSaveFile = sample.Any(f =>
                SaveExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            if (hasSaveFile)
            {
                return true;
            }

            var lower = directory.ToLowerInvariant();
            return SaveNameHints.Any(hint => lower.Contains(hint, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static IEnumerable<string> BuildNameVariants(string name, string? installDirName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var cleaned = value
                .Replace("™", string.Empty, StringComparison.Ordinal)
                .Replace("®", string.Empty, StringComparison.Ordinal)
                .Replace("©", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (cleaned.Length > 0)
            {
                set.Add(cleaned);
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(cleaned.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (sanitized.Length > 0)
            {
                set.Add(sanitized);
            }

            if (cleaned.Contains(':', StringComparison.Ordinal))
            {
                Add(cleaned[..cleaned.IndexOf(':')].Trim());
            }
        }

        Add(name);
        Add(installDirName);

        // "Game Deluxe Edition" → "Game"（只在括号/版本词之前有明显分隔时裁）
        foreach (var suffix in new[] { " Definitive Edition", " Deluxe Edition", " Ultimate Edition", " Gold Edition" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                Add(name[..^suffix.Length].Trim());
            }
        }

        return set;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
