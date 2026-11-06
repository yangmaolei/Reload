using System.Text.Json;
using System.Xml.Linq;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Locator;

/// <summary>
/// Xbox / 微软商店游戏定位器。覆盖两条来源：
/// <list type="number">
/// <item>
/// <b>MSIX(GDK) 游戏包</b>：Xbox 应用、Game Pass、主机游戏在 PC 上运行。
/// 存档在 <c>%LOCALAPPDATA%\Packages\&lt;PFN&gt;\SystemAppData\wgs</c>（云存档容器）。
/// </item>
/// <item>
/// <b>Xbox 原生游戏库</b>（<c>&lt;盘符&gt;\XboxGames\&lt;游戏名&gt;</c>）：Gaming Services 装的 PC 版。
/// 云存档在 <c>&lt;库根&gt;\GameSave\pgs\&lt;xuid&gt;_&lt;titleId&gt;</c>，靠 pgs 里的 AUMID / titleId 归属到具体游戏。
/// </item>
/// </list>
/// <para>
/// 判定"这是不是一个游戏"靠三个硬信号（微软商店游戏包的共同特征）：
/// 清单里的 <c>Executable="GameLaunchHelper.exe"</c>、<c>ms-xbl-&lt;titleId&gt;</c> 协议扩展、
/// 以及安装根目录下的 <c>MicrosoftGame.Config</c>（GDK 配置）。普通商店应用一个都不会有。
/// </para>
/// </summary>
public sealed class XboxLocator
{
    /// <summary>微软商店游戏包的启动器可执行文件名（出现在清单的 Application/@Executable 上）。</summary>
    private const string GameLaunchHelperExe = "GameLaunchHelper.exe";

    /// <summary>GDK 原生游戏的配置文件，位于包根目录。</summary>
    private const string GdkConfigFileName = "MicrosoftGame.Config";

    private const string WgsContainerFolder = "wgs";

    private const string LocalStateFolder = "LocalState";

    private const string SystemAppDataFolder = "SystemAppData";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>这些是 Xbox / 微软的运行环境组件，不是游戏，即使带 wgs 也要排除。</summary>
    private static readonly string[] NonGamePackagePrefixes =
    {
        "Microsoft.GamingApp",
        "Microsoft.GamingServices",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.XboxGamingOverlay",
        "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.XboxGameOverlay",
        "Microsoft.Xbox.TCUI",
        "Microsoft.XboxLiveAuth",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.VCLibs",
        "Microsoft.UI.Xaml",
        "Microsoft.NET.Native",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.WindowsStore",
        "Microsoft.LockApp",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.OneDriveSync",
    };

    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;

    public XboxLocator(IFileSystem fileSystem, ILocatorEnvironment environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<DiscoveredGame> Scan(out IReadOnlyList<string> warnings)
    {
        var games = new List<DiscoveredGame>();
        var issues = new List<string>();

        // 归属索引：PFN / 应用 ID / 标题（归一化后）→ 游戏
        var byPackageFamily = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);
        var byApplicationId = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);
        var byTitleId = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<UwpPackage> packages;
        try
        {
            packages = _environment.GetUwpPackages();
        }
        catch (Exception ex)
        {
            warnings = new[] { $"读取 UWP 包列表失败：{ex.Message}" };
            return games;
        }

        // ── 1. 解析每个包的清单，识别并组装 MSIX 游戏 ─────────────────────
        foreach (var package in packages)
        {
            if (IsExplicitNonGame(package.PackageFamilyName))
            {
                continue;
            }

            var manifest = ReadManifest(package.InstallLocation);
            if (!IsGamePackage(package, manifest))
            {
                continue;
            }

            var game = BuildPackageGame(package, manifest);
            if (game is null)
            {
                continue;
            }

            games.Add(game);

            if (!string.IsNullOrWhiteSpace(game.ExternalId))
            {
                byPackageFamily[game.ExternalId] = game;
            }

            if (manifest?.ApplicationId is { Length: > 0 } applicationId)
            {
                // "Game" 这种通用 ID 会撞车，只有有区分度的才进索引
                var key = Normalize(applicationId);
                if (key.Length >= 4 && !key.Equals("game", StringComparison.Ordinal))
                {
                    byApplicationId[key] = game;
                }
            }

            if (manifest?.TitleId is { Length: > 0 } titleId)
            {
                // Title ID 直接对应云存档容器目录名（wgs\<titleId>）
                byTitleId[titleId] = game;
            }

            RegisterTitle(byTitle, game.Name, game);
            if (manifest?.IdentityName is { Length: > 0 } identityName)
            {
                RegisterTitle(byTitle, identityName, game);
            }
        }

        // ── 2. 扫 Xbox 原生游戏库目录（<盘符>\XboxGames\<游戏名>）─────────
        //    要先于云存档归属，否则"只有原生安装、没有 MSIX 包"的游戏会拿不到自己的存档。
        foreach (var native in ScanNativeLibrary(games, byTitle))
        {
            games.Add(native);
            RegisterTitle(byTitle, native.Name, native);
        }

        // ── 3. 扫 Xbox 云存档目录（pgs / wgs）────────────────────────────
        var cloudSaves = ScanCloudSaves(issues);

        // ── 4. 把云存档挂到对应游戏上 ────────────────────────────────────
        foreach (var save in cloudSaves)
        {
            var target = ResolveCloudSaveOwner(save, byPackageFamily, byApplicationId, byTitle, byTitleId);
            if (target is null)
            {
                continue;
            }

            if (!target.SuggestedRoots.Any(r => string.Equals(r, save.Directory, StringComparison.OrdinalIgnoreCase)))
            {
                target.SuggestedRoots.Add(save.Directory);
            }
        }

        warnings = issues;
        return games;
    }
    // ────────────────────────── MSIX 游戏包 ──────────────────────────

    private DiscoveredGame? BuildPackageGame(UwpPackage package, XboxManifestInfo? manifest)
    {
        var pfn = package.PackageFamilyName;
        if (string.IsNullOrWhiteSpace(pfn))
        {
            return null;
        }

        var name = ResolveDisplayName(package, manifest);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var packageDataRoot = Path.Combine(_environment.LocalAppData, "Packages", pfn);

        var game = new DiscoveredGame
        {
            Name = name,
            Source = GameSource.Xbox,
            ExternalId = pfn,
            InstallDir = string.IsNullOrWhiteSpace(package.InstallLocation) ? null : package.InstallLocation,
            Engine = EngineDetector.Detect(_fileSystem, package.InstallLocation),
        };

        game.ExeNames.AddRange(LocatorIo.FindExeNamesDeep(_fileSystem, package.InstallLocation));

        // 云存档容器：MSIX 游戏的存档固定在包数据目录的 SystemAppData\wgs
        var wgs = Path.Combine(packageDataRoot, SystemAppDataFolder, WgsContainerFolder);
        if (_fileSystem.DirectoryExists(wgs))
        {
            game.SuggestedRoots.Add(wgs);
        }

        // 有些游戏把额外存档放在 LocalState
        var localState = Path.Combine(packageDataRoot, LocalStateFolder);
        if (_fileSystem.DirectoryExists(localState) && LocatorIo.DirectoryHasAnyFile(_fileSystem, localState))
        {
            game.SuggestedRoots.Add(localState);
        }

        if (manifest?.TitleId is { Length: > 0 } titleId)
        {
            game.Notes.Add($"Xbox Title ID：{titleId}");
        }

        if (game.SuggestedRoots.Count == 0)
        {
            game.Notes.Add("尚未生成云存档；游戏首次保存后存档会自动出现");
        }

        return game;
    }

    /// <summary>包的清单信息（只取定位需要的字段）。</summary>
    private sealed record XboxManifestInfo(
        string? ApplicationId,
        string? TitleId,
        bool HasGameLauncher,
        string? DisplayName,
        string? IdentityName,
        bool IsAddon);

    private XboxManifestInfo? ReadManifest(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        var text = LocatorIo.TryReadText(_fileSystem, Path.Combine(installLocation, "AppxManifest.xml"));
        if (text is null)
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(text);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            var identity = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
            var properties = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var displayName = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

            var application = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Applications")?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Application");

            var applicationId = application?.Attribute("Id")?.Value;
            var executable = application?.Attribute("Executable")?.Value;

            string? titleId = null;
            foreach (var extension in root.Descendants().Where(e => e.Name.LocalName == "Extension"))
            {
                var protocol = extension.Elements().FirstOrDefault(e => e.Name.LocalName == "Protocol");
                var name = protocol?.Attribute("Name")?.Value;
                if (name is not null && name.StartsWith("ms-xbl-", StringComparison.OrdinalIgnoreCase))
                {
                    titleId = name["ms-xbl-".Length..];
                    break;
                }
            }

            return new XboxManifestInfo(
                applicationId,
                titleId,
                string.Equals(executable, GameLaunchHelperExe, StringComparison.OrdinalIgnoreCase),
                displayName,
                identity?.Attribute("Name")?.Value,
                IsAddonPackage(root));
        }
        catch
        {
            // 清单损坏/权限不足时按"没有额外信息"处理，不影响其他包
            return null;
        }
    }

    /// <summary>运行时框架依赖（VCLibs / Xaml / .NET Native）不算"依附于某个游戏"。</summary>
    private static readonly string[] FrameworkDependencies =
    {
        "Microsoft.VCLibs",
        "Microsoft.UI.Xaml",
        "Microsoft.NET.Native",
        "Microsoft.WindowsAppRuntime",
    };

    /// <summary>
    /// 附属包（DLC / 扩展包）会声明对主游戏包的依赖；本体游戏只依赖运行时框架。
    /// 附属包不是独立游戏，不该出现在游戏列表里。
    /// </summary>
    private static bool IsAddonPackage(XElement root)
    {
        var dependencies = root.Elements()
            .Where(e => e.Name.LocalName == "Dependencies")
            .SelectMany(e => e.Elements())
            .Where(e => e.Name.LocalName == "PackageDependency")
            .Select(e => e.Attribute("Name")?.Value)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        return dependencies.Any(name => !FrameworkDependencies.Any(framework =>
            name.StartsWith(framework, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsGamePackage(UwpPackage package, XboxManifestInfo? manifest)
    {
        var install = package.InstallLocation;

        // 没装（卸载后残留的注册表项、安装目录被清掉的侧载包）不算游戏
        if (string.IsNullOrWhiteSpace(install) || !_fileSystem.DirectoryExists(install))
        {
            return false;
        }

        if (package.IsGame)
        {
            return true;
        }

        if (manifest is not null)
        {
            // 附属包（DLC / 扩展包）依附于主游戏，不是独立游戏
            if (manifest.IsAddon)
            {
                return false;
            }

            // 商店游戏模板：清单里的启动器就是 GameLaunchHelper.exe
            if (manifest.HasGameLauncher)
            {
                return true;
            }

            // ms-xbl-<titleId> 协议扩展 = Xbox Live 游戏
            if (!string.IsNullOrWhiteSpace(manifest.TitleId))
            {
                return true;
            }
        }

        // GDK 原生游戏：包根目录带 MicrosoftGame.Config
        if (_fileSystem.FileExists(Path.Combine(install, GdkConfigFileName)))
        {
            return true;
        }

        // 兜底：已经生成过 wgs 云存档容器的包
        var wgs = Path.Combine(_environment.LocalAppData, "Packages", package.PackageFamilyName,
            SystemAppDataFolder, WgsContainerFolder);
        return _fileSystem.DirectoryExists(wgs);
    }

    private static bool IsExplicitNonGame(string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return true;
        }

        return NonGamePackagePrefixes.Any(prefix =>
            packageFamilyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveDisplayName(UwpPackage package, XboxManifestInfo? manifest)
    {
        // 1) 清单里的字面显示名（最准）
        if (manifest?.DisplayName is { Length: > 0 } manifestName && IsUsableDisplayName(manifestName))
        {
            return manifestName.Trim();
        }

        // 2) 注册表里的 DisplayName
        if (IsUsableDisplayName(package.DisplayName))
        {
            return package.DisplayName.Trim();
        }

        // 3) 清单里的 Identity/Name（例如 CRITICALREFLEX.BuckshotRoulette）
        if (manifest?.IdentityName is { Length: > 0 } identity)
        {
            var tail = identity.Contains('.', StringComparison.Ordinal)
                ? identity[(identity.LastIndexOf('.') + 1)..]
                : identity;
            if (tail.Length > 0)
            {
                return tail;
            }
        }

        // 4) PFN 去掉发布者后缀
        var pfn = package.PackageFamilyName;
        var underscore = pfn.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? pfn[..underscore] : pfn;
    }

    /// <summary>ms-resource:xxx / @xxx 这类资源引用不能直接当名字用。</summary>
    private static bool IsUsableDisplayName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith('@') &&
        !value.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase);

    // ────────────────────────── 云存档（pgs / wgs）──────────────────────────

    /// <summary>一条 Xbox 云存档记录。</summary>
    private sealed record XboxCloudSave(string? PackageFamilyName, string? ApplicationId, string? TitleId, string Directory);

    private IReadOnlyList<XboxCloudSave> ScanCloudSaves(List<string> issues)
    {
        var result = new List<XboxCloudSave>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in SafeList(() => _environment.GetXboxLibraryRoots()))
        {
            var gameSave = Path.Combine(root, "GameSave");

            // pgs\<xuid>_<titleId>\ —— 原生 PC 游戏的云存档
            foreach (var dir in SafeList(() => _fileSystem.EnumerateDirectories(
                         Path.Combine(gameSave, "pgs"), recursive: false)))
            {
                var folderName = Path.GetFileName(dir);
                var titleId = ExtractTitleId(folderName);
                var (pfn, applicationId) = ReadCloudSaveIdentity(dir, issues);

                if (seen.Add(dir))
                {
                    result.Add(new XboxCloudSave(pfn, applicationId, titleId, dir));
                }
            }

            // wgs\<titleId>\ —— 原生 PC 游戏的 wgs 容器
            foreach (var dir in SafeList(() => _fileSystem.EnumerateDirectories(
                         Path.Combine(gameSave, WgsContainerFolder), recursive: false)))
            {
                var folderName = Path.GetFileName(dir);
                var titleId = ExtractTitleId(folderName);
                if (seen.Add(dir))
                {
                    result.Add(new XboxCloudSave(null, null, titleId, dir));
                }
            }        }

        return result;
    }

    /// <summary>
    /// 取 titleId。两种目录名都见过：
    /// <c>u_2535467423605544_16D460</c>（pgs，带 xuid 前缀）与 <c>636F5860</c>（wgs，纯 titleId）。
    /// </summary>
    private static string? ExtractTitleId(string folderName)
    {
        var index = folderName.LastIndexOf('_');
        var tail = index >= 0 && index < folderName.Length - 1
            ? folderName[(index + 1)..]
            : folderName;

        return tail.Length >= 4 && tail.All(Uri.IsHexDigit) ? tail : null;
    }

    /// <summary>
    /// 读 pgs 目录里的元数据 JSON，取 Context.Aumid（形如 PkgFamily!AppId）。
    /// 文件名不固定（有的是 <c>&lt;xuid&gt;_&lt;titleId&gt;.json</c>，有的是去掉 <c>u_</c> 前缀的），
    /// 所以直接把目录里的 json 都扫一遍，取第一个带 Context 的。
    /// </summary>
    private (string? PackageFamilyName, string? ApplicationId) ReadCloudSaveIdentity(
        string directory,
        List<string> issues)
    {
        foreach (var path in LocatorIo.FilesWithExtensions(_fileSystem, directory, false, ".json"))
        {
            var text = LocatorIo.TryReadText(_fileSystem, path);
            if (text is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text, JsonOptions);
                if (!document.RootElement.TryGetProperty("Context", out var context) ||
                    context.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var aumid = context.TryGetProperty("Aumid", out var aumidElement) &&
                            aumidElement.ValueKind == JsonValueKind.String
                    ? aumidElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(aumid))
                {
                    continue;
                }

                var separator = aumid!.IndexOf('!');
                if (separator < 0)
                {
                    return (null, aumid);
                }

                return (aumid[..separator], aumid[(separator + 1)..]);
            }
            catch (JsonException ex)
            {
                issues.Add($"解析 Xbox 云存档元数据失败：{Path.GetFileName(path)}（{ex.Message}）");
            }
        }

        return (null, null);
    }

    private static DiscoveredGame? ResolveCloudSaveOwner(
        XboxCloudSave save,
        IReadOnlyDictionary<string, DiscoveredGame> byPackageFamily,
        IReadOnlyDictionary<string, DiscoveredGame> byApplicationId,
        IReadOnlyDictionary<string, DiscoveredGame> byTitle,
        IReadOnlyDictionary<string, DiscoveredGame> byTitleId)
    {
        // 1) AUMID 里的包族名 → 最精确
        if (!string.IsNullOrWhiteSpace(save.PackageFamilyName) &&
            byPackageFamily.TryGetValue(save.PackageFamilyName, out var byPackage))
        {
            return byPackage;
        }

        // 2) AUMID 里的应用 ID（如 Forzahorizon6）
        if (!string.IsNullOrWhiteSpace(save.ApplicationId))
        {
            var key = Normalize(save.ApplicationId);
            if (byApplicationId.TryGetValue(key, out var byApplication))
            {
                return byApplication;
            }

            // 3) 应用 ID 与游戏名/原生库目录名对上（原生安装的游戏没有 MSIX 包时走这条）
            if (byTitle.TryGetValue(key, out var byTitleMatch))
            {
                return byTitleMatch;
            }
        }

        // 4) Title ID（wgs\<titleId> 容器目录名直接就是它）
        if (!string.IsNullOrWhiteSpace(save.TitleId) &&
            byTitleId.TryGetValue(save.TitleId, out var byId))
        {
            return byId;
        }

        return null;
    }

    // ────────────────────────── 原生游戏库目录 ──────────────────────────

    private IEnumerable<DiscoveredGame> ScanNativeLibrary(
        IReadOnlyList<DiscoveredGame> existing,
        IReadOnlyDictionary<string, DiscoveredGame> byTitle)
    {
        var results = new List<DiscoveredGame>();

        foreach (var root in SafeList(() => _environment.GetXboxLibraryRoots()))
        {
            foreach (var dir in SafeList(() => _fileSystem.EnumerateDirectories(root, recursive: false)))
            {
                var title = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(title) ||
                    title.Equals("GameSave", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 已经有对应的 MSIX 包了（同一款游戏），不重复建条目
                if (byTitle.ContainsKey(Normalize(title)) ||
                    existing.Any(g => TitlesMatch(g.Name, title)) ||
                    results.Any(g => TitlesMatch(g.Name, title)))
                {
                    continue;
                }

                var game = new DiscoveredGame
                {
                    Name = title,
                    Source = GameSource.Xbox,
                    ExternalId = "xbox-native:" + title,
                    InstallDir = dir,
                    Engine = EngineDetector.Detect(_fileSystem, dir),
                };

                game.ExeNames.AddRange(LocatorIo.FindExeNamesDeep(_fileSystem, dir));
                game.Notes.Add("Xbox 原生游戏（云存档位于 GameSave 目录）");

                results.Add(game);
            }
        }

        return results;
    }

    /// <summary>把标题登记到索引（归一化后比较），重复的标题以先注册者为准。</summary>
    private static void RegisterTitle(Dictionary<string, DiscoveredGame> index, string title, DiscoveredGame game)
    {
        var key = Normalize(title);
        if (key.Length == 0)
        {
            return;
        }

        if (!index.ContainsKey(key))
        {
            index[key] = game;
        }
    }

    private static bool TitlesMatch(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.Equals(b, StringComparison.Ordinal) ||
               a.Contains(b, StringComparison.Ordinal) ||
               b.Contains(a, StringComparison.Ordinal);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private IReadOnlyList<string> SafeList(Func<IReadOnlyList<string>> factory)
    {
        try
        {
            return factory() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
