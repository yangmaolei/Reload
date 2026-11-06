using GameSaveSync.Core.Models;

namespace GameSaveSync.Core.Locator;

/// <summary>探测到的游戏引擎。规则库按引擎给出常见存档模板。</summary>
public enum GameEngine
{
    Unknown = 0,
    Unity = 1,
    Unreal = 2,
    RpgMaker = 3,
    RenPy = 4,
    GameMaker = 5,
}

/// <summary>平台库扫描出来的一个游戏候选（尚未入库）。</summary>
public sealed class DiscoveredGame
{
    public required string Name { get; init; }

    public GameSource Source { get; init; } = GameSource.Manual;

    /// <summary>Steam AppId / Epic CatalogItemId / GOG gameId / UWP PackageFamilyName。</summary>
    public string? ExternalId { get; init; }

    public string? InstallDir { get; init; }

    public List<string> ExeNames { get; init; } = new();

    /// <summary>候选存档目录（绝对路径，来自规则库 + 引擎雷达）。</summary>
    public List<string> SuggestedRoots { get; init; } = new();

    public GameEngine Engine { get; set; } = GameEngine.Unknown;

    public string PlatformLabel => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic",
        GameSource.Gog => "GOG",
        GameSource.Ubisoft => "Ubisoft",
        GameSource.Uwp => "UWP",
        GameSource.Xbox => "Xbox",
        _ => "手动",
    };

    /// <summary>已经在库里（按名称或安装目录匹配），UI 默认不再勾选。</summary>
    public bool AlreadyAdded { get; set; }

    /// <summary>扫描过程中的说明（例如"未找到存档目录，可用探测模式"）。</summary>
    public List<string> Notes { get; } = new();

    public override string ToString() => $"{Name} [{PlatformLabel}] roots={SuggestedRoots.Count}";
}

/// <summary>平台发现的结果。</summary>
public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredGame> Games,
    int LibraryCount,
    IReadOnlyList<string> Warnings);

/// <summary>一个 UWP/MSIX 包（注册表 AppModel Repository 里读出来的）。</summary>
public sealed record UwpPackage(
    string PackageFamilyName,
    string DisplayName,
    string? InstallLocation)
{
    /// <summary>
    /// 完整包名（含版本与架构），注册表里的原始键名。
    /// 例如 <c>TeamCherry.HollowKnightSilksong_1.0.30000.0_x64__y4jvztpgccj42</c>；
    /// 它的 <see cref="PackageFamilyName"/> 是 <c>TeamCherry.HollowKnightSilksong_y4jvztpgccj42</c>。
    /// </summary>
    public string PackageFullName { get; init; } = PackageFamilyName;

    /// <summary>是否看起来是游戏（GDK 的 MicrosoftGame.Config，或带 wgs 云存档容器）。</summary>
    public bool IsGame { get; init; }
}

/// <summary>
/// 发现流程需要知道的"本机环境"：标准目录 + 平台安装位置 + UWP 包列表。
/// Core 不直接读注册表/环境变量，Windows 实现放在 Infrastructure，测试用假实现。
/// </summary>
public interface ILocatorEnvironment
{
    string UserProfile { get; }

    string AppData { get; }

    string LocalAppData { get; }

    string Documents { get; }

    string SavedGames { get; }

    string ProgramData { get; }

    /// <summary>Steam 安装根（可能有多个，如 C:\Program Files (x86)\Steam）。</summary>
    IReadOnlyList<string> GetSteamRoots();

    /// <summary>Epic 清单目录（通常是 %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests）。</summary>
    IReadOnlyList<string> GetEpicManifestDirs();

    /// <summary>GOG 游戏目录（Program Files\GOG Games、GOG Galaxy\Games 等）。</summary>
    IReadOnlyList<string> GetGogGameDirs();

    /// <summary>Ubisoft 存档根（%LOCALAPPDATA%\Ubisoft Game Launcher\savegames）。</summary>
    IReadOnlyList<string> GetUbisoftSaveRoots();

    IReadOnlyList<UwpPackage> GetUwpPackages();

    /// <summary>
    /// Xbox 游戏库根目录（Xbox 应用安装的原生游戏，通常是 <c>C:\XboxGames</c>）。
    /// 里面每个子目录是一个游戏，云存档在 <c>&lt;根&gt;\GameSave</c> 下。
    /// </summary>
    IReadOnlyList<string> GetXboxLibraryRoots();

    /// <summary>额外路径令牌（例如 %STEAMUSERDATA%），会并入 PathTokenResolver。</summary>
    IReadOnlyDictionary<string, string> GetExtraPathTokens();
}
