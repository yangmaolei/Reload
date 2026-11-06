using System.Runtime.Versioning;
using System.Xml.Linq;
using GameSaveSync.Core.Locator;
using Microsoft.Win32;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// 真实环境：标准目录来自 Environment，Steam 路径来自注册表（per-user 与 machine 两处），
/// UWP/MSIX 包列表来自 AppModel Repository，Xbox 游戏库来自磁盘上的 <c>XboxGames</c> 目录。
/// 全部只读，且任何一项失败都退回空集合。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLocatorEnvironment : ILocatorEnvironment
{
    /// <summary>注册表里 UWP 包的读取位置（per-user 与 machine 各一份）。</summary>
    private const string PackageRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>GDK 游戏的标志文件。</summary>
    private const string GdkConfigFileName = "MicrosoftGame.Config";

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string SavedGames => Path.Combine(UserProfile, "Saved Games");

    public string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public IReadOnlyList<string> GetSteamRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = path.Trim().Trim('"');
            if (normalized.Length > 0 && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(normalized);
            }
        }

        Add(ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
        Add(ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        Add(ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            Add(Path.Combine(programFilesX86, "Steam"));
        }

        return roots.Where(Directory.Exists).ToList();
    }

    public IReadOnlyList<string> GetEpicManifestDirs()
    {
        var dir = Path.Combine(ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return Directory.Exists(dir) ? new[] { dir } : Array.Empty<string>();
    }

    public IReadOnlyList<string> GetGogGameDirs()
    {
        var candidates = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            Add(Path.Combine(programFilesX86, "GOG Games"));
            Add(Path.Combine(programFilesX86, "GOG Galaxy", "Games"));
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            Add(Path.Combine(programFiles, "GOG Games"));
        }

        // 具体游戏路径在注册表子键里，但目录扫描更稳（也覆盖便携版 GOG 游戏）

        return candidates;
    }

    public IReadOnlyList<string> GetUbisoftSaveRoots()
    {
        var dir = Path.Combine(LocalAppData, "Ubisoft Game Launcher", "savegames");
        return Directory.Exists(dir) ? new[] { dir } : Array.Empty<string>();
    }

    /// <summary>
    /// 枚举本机已注册的 UWP/MSIX 包。
    /// <para>
    /// 注册表里的键名是<b>完整包名</b>（<c>Name_Version_Arch__PublisherId</c>），
    /// 而包数据目录用的却是<b>包族名</b>（<c>Name_PublisherId</c>）——两者差一个版本段，
    /// 早期版本直接拿键名拼路径，导致存档目录永远找不到。
    /// </para>
    /// </summary>
    public IReadOnlyList<UwpPackage> GetUwpPackages()
    {
        var result = new List<UwpPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, subKey) in new[]
                 {
                     (Registry.CurrentUser, PackageRepositoryKey),
                     (Registry.LocalMachine, PackageRepositoryKey),
                 })
        {
            foreach (var package in ReadPackages(root, subKey))
            {
                if (seen.Add(package.PackageFamilyName))
                {
                    result.Add(package);
                }
            }
        }

        return result;
    }

    private IEnumerable<UwpPackage> ReadPackages(RegistryKey hive, string subKey)
    {
        var packages = new List<UwpPackage>();

        try
        {
            using var key = hive.OpenSubKey(subKey);
            if (key is null)
            {
                return packages;
            }

            foreach (var fullName in key.GetSubKeyNames())
            {
                using var package = key.OpenSubKey(fullName);
                if (package is null)
                {
                    continue;
                }

                var familyName = DerivePackageFamilyName(fullName);
                if (string.IsNullOrWhiteSpace(familyName))
                {
                    continue;
                }

                var installLocation = package.GetValue("PackageRootFolder") as string;
                var displayName = ResolveDisplayName(package.GetValue("DisplayName") as string, installLocation);

                packages.Add(new UwpPackage(familyName, displayName, installLocation)
                {
                    PackageFullName = fullName,
                    IsGame = IsGamePackage(installLocation, familyName),
                });
            }
        }
        catch
        {
            // 注册表被策略限制时不影响其他平台
        }

        return packages;
    }

    /// <summary>
    /// 从完整包名派生包族名：<c>Name_Version_Arch__PublisherId</c> → <c>Name_PublisherId</c>。
    /// 已经长得像包族名的（只有两段）原样返回。
    /// </summary>
    public static string DerivePackageFamilyName(string fullPackageName)
    {
        if (string.IsNullOrWhiteSpace(fullPackageName))
        {
            return string.Empty;
        }

        var parts = fullPackageName.Split('_');
        if (parts.Length < 3)
        {
            return fullPackageName;
        }

        var publisher = parts[^1];
        if (publisher.Length == 0 || IsArchitectureToken(publisher))
        {
            return fullPackageName;
        }

        return parts[0] + "_" + publisher;
    }

    private static bool IsArchitectureToken(string value) =>
        value.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("arm", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("arm64", StringComparison.OrdinalIgnoreCase);

    /// <summary>注册表里的 DisplayName 可能是 ms-resource 引用，这时去清单里取真名。</summary>
    private static string ResolveDisplayName(string? registryValue, string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(registryValue) &&
            !registryValue.StartsWith('@') &&
            !registryValue.Contains("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return registryValue;
        }

        var manifestName = ReadManifestDisplayName(installLocation);
        if (!string.IsNullOrWhiteSpace(manifestName))
        {
            return manifestName;
        }

        if (!string.IsNullOrWhiteSpace(registryValue) && !registryValue.StartsWith('@'))
        {
            return registryValue;
        }

        return string.Empty;
    }

    private static string? ReadManifestDisplayName(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(installLocation, "AppxManifest.xml");
            if (!File.Exists(path))
            {
                return null;
            }

            var document = XDocument.Load(path);
            var properties = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var displayName = properties?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

            return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        }
        catch
        {
            // WindowsApps 目录可能没有读权限，读不到就退回包族名
            return null;
        }
    }

    /// <summary>
    /// 粗判是不是游戏：包根目录带 GDK 配置（MicrosoftGame.Config）才算。
    /// 其它判定（清单里的 GameLaunchHelper / ms-xbl 协议、附属包排除）由 Core 的 XboxLocator 负责。
    /// </summary>
    private static bool IsGamePackage(string? installLocation, string familyName)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        try
        {
            return Directory.Exists(installLocation) &&
                   File.Exists(Path.Combine(installLocation, GdkConfigFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Xbox 游戏库根目录（<c>&lt;盘符&gt;\XboxGames</c>）。
    /// Xbox 应用允许把游戏装到别的盘，所以固定盘都要看一眼。
    /// </summary>
    public IReadOnlyList<string> GetXboxLibraryRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = path.Trim().TrimEnd('\\', '/');
            if (normalized.Length > 0 && !roots.Contains(normalized, StringComparer.OrdinalIgnoreCase) &&
                Directory.Exists(normalized))
            {
                roots.Add(normalized);
            }
        }

        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                Add(Path.Combine(systemRoot, "XboxGames"));
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
            }
        }
        catch
        {
            // 取不到磁盘列表就只用系统盘
        }

        return roots;
    }

    public IReadOnlyDictionary<string, string> GetExtraPathTokens()
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in GetSteamRoots())
        {
            var userData = Path.Combine(steamRoot, "userdata");
            if (Directory.Exists(userData))
            {
                tokens["%STEAMUSERDATA%"] = userData;
                break;
            }
        }

        var xboxLibrary = GetXboxLibraryRoots().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xboxLibrary))
        {
            tokens["%XBOXLIBRARY%"] = xboxLibrary;
        }

        return tokens;
    }

    private static string? ReadRegistry(RegistryKey root, string subKey, string? valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return null;
            }

            return valueName is null ? key.GetValue(null) as string : key.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
