using GameSaveSync.Core.Abstractions;

namespace GameSaveSync.Core.Locator;

/// <summary>
/// 引擎识别：只看安装目录里的标志性文件/子目录，不扫全盘。
/// 识别结果决定规则库给哪些常见存档模板（Unity LocalLow / Unreal Saved / RPG Maker / RenPy）。
/// </summary>
public static class EngineDetector
{
    public static GameEngine Detect(IFileSystem fileSystem, string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !fileSystem.DirectoryExists(installDir))
        {
            return GameEngine.Unknown;
        }

        try
        {
            var topFiles = SafeFiles(fileSystem, installDir, recursive: false);
            var topDirs = SafeDirs(fileSystem, installDir);

            if (topFiles.Any(f => Path.GetFileName(f).Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.Unity;
            }

            if (topDirs.Any(d => Path.GetFileName(d).EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) &&
                topFiles.Any(f => Path.GetFileName(f).Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.Unity;
            }

            if (topFiles.Any(f => Path.GetFileName(f).Equals("data.win", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.GameMaker;
            }

            var www = Path.Combine(installDir, "www");
            if (fileSystem.DirectoryExists(www) &&
                (fileSystem.FileExists(Path.Combine(www, "js", "rpg_core.js")) ||
                 fileSystem.FileExists(Path.Combine(www, "js", "rpg_managers.js"))))
            {
                return GameEngine.RpgMaker;
            }

            var js = Path.Combine(installDir, "js");
            if (fileSystem.FileExists(Path.Combine(js, "rpg_core.js")))
            {
                return GameEngine.RpgMaker;
            }

            var gameDir = Path.Combine(installDir, "game");
            if (fileSystem.DirectoryExists(gameDir) &&
                SafeFiles(fileSystem, gameDir, recursive: false).Any(f => f.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.RenPy;
            }

            if (topFiles.Any(f => Path.GetFileName(f).Equals("renpy.exe", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.RenPy;
            }

            if (topDirs.Any(d => Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase)) ||
                SafeDirs(fileSystem, installDir).Any(d =>
                    fileSystem.DirectoryExists(Path.Combine(d, "Content", "Paks"))))
            {
                return GameEngine.Unreal;
            }

            if (topFiles.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)) &&
                topDirs.Any(d => Path.GetFileName(d).Equals("Binaries", StringComparison.OrdinalIgnoreCase)))
            {
                return GameEngine.Unreal;
            }
        }
        catch
        {
            // 权限/占用都不影响"识别不出来"这个结论
        }

        return GameEngine.Unknown;
    }

    private static IReadOnlyList<string> SafeFiles(IFileSystem fileSystem, string directory, bool recursive)
    {
        try
        {
            return fileSystem.EnumerateFiles(directory, recursive);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> SafeDirs(IFileSystem fileSystem, string directory)
    {
        try
        {
            return fileSystem.EnumerateDirectories(directory, recursive: false);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
