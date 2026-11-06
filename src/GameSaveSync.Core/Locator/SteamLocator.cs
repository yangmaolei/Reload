using System.Text;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Locator;

/// <summary>Locator 共用的小工具：读文本、按后缀枚举、挑 exe 名。</summary>
internal static class LocatorIo
{
    private static readonly string[] ExeNoise =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "dxwebsetup", "crash", "report",
        "launcher_installer", "dotnet", "ue4prereqsetup", "ue5prereqsetup", "easyanticheat",
        "eac_", "battleye", "unitycrashhandler",
    };

    public static string? TryReadText(IFileSystem fileSystem, string path)
    {
        try
        {
            if (!fileSystem.FileExists(path))
            {
                return null;
            }

            using var stream = fileSystem.OpenRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> FilesWithExtensions(
        IFileSystem fileSystem,
        string directory,
        bool recursive,
        params string[] extensions)
    {
        try
        {
            if (!fileSystem.DirectoryExists(directory))
            {
                return Array.Empty<string>();
            }

            return fileSystem.EnumerateFiles(directory, recursive)
                .Where(path => extensions.Length == 0 ||
                               extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static List<string> FindExeNames(IFileSystem fileSystem, string? installDir)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(installDir) || !fileSystem.DirectoryExists(installDir))
        {
            return result;
        }

        var files = FilesWithExtensions(fileSystem, installDir, recursive: false, ".exe");
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (ExeNoise.Any(noise => name.Contains(noise, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(name);

            if (result.Count >= 5)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>安装目录下最多取两层子目录里的 exe（有些游戏把主程序放在 bin\win64）。</summary>
    public static List<string> FindExeNamesDeep(IFileSystem fileSystem, string? installDir)
    {
        var direct = FindExeNames(fileSystem, installDir);
        if (direct.Count > 0 || string.IsNullOrWhiteSpace(installDir))
        {
            return direct;
        }

        try
        {
            var subDirs = fileSystem.EnumerateDirectories(installDir, recursive: false)
                .Take(12)
                .ToList();

            foreach (var dir in subDirs)
            {
                var names = FindExeNames(fileSystem, dir);
                if (names.Count > 0)
                {
                    return names;
                }
            }
        }
        catch
        {
            // 权限问题不影响整体扫描
        }

        return direct;
    }

    public static bool DirectoryHasAnyFile(IFileSystem fileSystem, string directory)
    {
        try
        {
            return fileSystem.DirectoryExists(directory) && fileSystem.EnumerateFiles(directory, false).Count > 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Steam：libraryfolders.vdf → appmanifest_*.acf → userdata/&lt;id&gt;/&lt;appid&gt;/remote。</summary>
public sealed class SteamLocator
{
    private readonly IFileSystem _fileSystem;
    private readonly ILocatorEnvironment _environment;

    public SteamLocator(IFileSystem fileSystem, ILocatorEnvironment environment)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public IReadOnlyList<DiscoveredGame> Scan(out IReadOnlyList<string> warnings)
    {
        var games = new List<DiscoveredGame>();
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in _environment.GetSteamRoots())
        {
            if (!_fileSystem.DirectoryExists(steamRoot))
            {
                continue;
            }

            foreach (var library in ReadLibraries(steamRoot, issues))
            {
                var steamApps = Path.Combine(library, "steamapps");
                foreach (var manifestPath in LocatorIo.FilesWithExtensions(_fileSystem, steamApps, false, ".acf"))
                {
                    if (!Path.GetFileName(manifestPath).StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var game = ParseManifest(steamRoot, steamApps, manifestPath, issues);
                    if (game is null)
                    {
                        continue;
                    }

                    if (seen.Add(game.ExternalId ?? game.Name))
                    {
                        games.Add(game);
                    }
                }
            }
        }

        warnings = issues;
        return games;
    }

    private IEnumerable<string> ReadLibraries(string steamRoot, List<string> issues)
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                libraries.Add(path);
            }
        }

        Add(steamRoot);

        var candidates = new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        };

        foreach (var configPath in candidates)
        {
            var text = LocatorIo.TryReadText(_fileSystem, configPath);
            if (text is null)
            {
                continue;
            }

            VdfNode? root;
            try
            {
                root = VdfParser.ParseSingle(text);
            }
            catch (Exception ex)
            {
                issues.Add($"解析 {configPath} 失败：{ex.Message}");
                continue;
            }

            var folders = root?.Child("libraryfolders") ?? root;
            if (folders is null)
            {
                continue;
            }

            foreach (var child in folders.Children)
            {
                // 新格式： "0" { "path" "D:\\SteamLibrary" ... }
                var path = child.ChildValue("path");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Add(path);
                    continue;
                }

                // 旧格式： "1" "D:\\SteamLibrary"
                if (!string.IsNullOrWhiteSpace(child.Value) && LooksLikePath(child.Value))
                {
                    Add(child.Value);
                }
            }
        }

        return libraries;
    }

    private static bool LooksLikePath(string value) =>
        value.Length >= 2 && (value[1] == ':' || value.StartsWith(@"\\", StringComparison.Ordinal));

    private DiscoveredGame? ParseManifest(string steamRoot, string steamApps, string manifestPath, List<string> issues)
    {
        var text = LocatorIo.TryReadText(_fileSystem, manifestPath);
        if (text is null)
        {
            return null;
        }

        VdfNode? state;
        try
        {
            state = VdfParser.ParseSingle(text)?.Child("AppState") ?? VdfParser.ParseSingle(text);
        }
        catch (Exception ex)
        {
            issues.Add($"解析 {Path.GetFileName(manifestPath)} 失败：{ex.Message}");
            return null;
        }

        if (state is null)
        {
            return null;
        }

        var name = state.ChildValue("name");
        var appId = state.ChildValue("appid");
        var installDirName = state.ChildValue("installdir");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        // StateFlags 4 = 已完整安装；2 = 已更新完成但可能未装全，仍然收进来
        var flagsText = state.ChildValue("StateFlags");
        if (int.TryParse(flagsText, out var flags) && (flags & 4) == 0 && (flags & 2) == 0)
        {
            return null;
        }

        var installDir = string.IsNullOrWhiteSpace(installDirName)
            ? null
            : Path.Combine(steamApps, "common", installDirName);

        var game = new DiscoveredGame
        {
            Name = name,
            Source = GameSource.Steam,
            ExternalId = appId,
            InstallDir = installDir,
            ExeNames = LocatorIo.FindExeNamesDeep(_fileSystem, installDir),
            Engine = EngineDetector.Detect(_fileSystem, installDir),
        };

        foreach (var remote in FindUserDataRoots(steamRoot, appId))
        {
            game.SuggestedRoots.Add(remote);
        }

        return game;
    }

    private IEnumerable<string> FindUserDataRoots(string steamRoot, string appId)
    {
        var userData = Path.Combine(steamRoot, "userdata");
        if (!_fileSystem.DirectoryExists(userData))
        {
            yield break;
        }

        string[] users;
        try
        {
            users = _fileSystem.EnumerateDirectories(userData, recursive: false).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var user in users)
        {
            var remote = Path.Combine(user, appId, "remote");
            if (_fileSystem.DirectoryExists(remote) && LocatorIo.DirectoryHasAnyFile(_fileSystem, remote))
            {
                yield return remote;
            }
        }
    }
}
