namespace GameSaveSync.Core.Configuration;

/// <summary>
/// 数据目录布局。<c>objects/&lt;hh&gt;/&lt;hash&gt;</c> 是本地内容寻址存储（CAS）。
/// 便携模式：exe 同级存在 <c>portable.flag</c> 时数据放 <c>&lt;exe&gt;\data</c>，不写注册表。
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    /// <summary>应用显示名与数据目录名（v0.4 起由 GameSaveSync 更名）。</summary>
    public const string AppName = "Reload";

    /// <summary>更名前的数据目录名，启动时自动迁移。</summary>
    public const string LegacyAppName = "GameSaveSync";

    /// <summary>
    /// WebDAV 远端根目录的默认值（新建用户首次配置时使用）。
    /// 已配置的用户用的是 settings 表里存的值，不受此默认值影响。
    /// </summary>
    public const string DefaultRemoteRoot = AppName;

    public AppPaths(string dataRoot, bool isPortable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        IsPortable = isPortable;
    }

    public string DataRoot { get; }

    /// <summary>便携模式：数据在程序目录，开机自启等注册表操作必须关闭。</summary>
    public bool IsPortable { get; }

    public string DatabasePath => Path.Combine(DataRoot, "gamesavesync.db");

    public string ObjectsDir => Path.Combine(DataRoot, "objects");

    /// <summary>对象暂存目录：先写临时名，校验后 rename 到 CAS，强杀不留半截对象。</summary>
    public string ObjectsTempDir => Path.Combine(ObjectsDir, "tmp");

    public string ManifestsDir => Path.Combine(DataRoot, "manifests");

    public string LogsDir => Path.Combine(DataRoot, "logs");

    public string CoversDir => Path.Combine(DataRoot, "covers");

    public string BucketDirectory(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length < 2)
        {
            throw new ArgumentException("哈希长度不足，无法分桶", nameof(hash));
        }

        return Path.Combine(ObjectsDir, hash[..2].ToLowerInvariant());
    }

    public string ObjectPath(string hash) =>
        Path.Combine(BucketDirectory(hash), hash.ToLowerInvariant());

    public string ManifestPath(string gameId, string fileName) =>
        Path.Combine(ManifestsDir, gameId.ToLowerInvariant(), fileName);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ObjectsDir);
        Directory.CreateDirectory(ObjectsTempDir);
        Directory.CreateDirectory(ManifestsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CoversDir);
    }

    /// <summary>默认数据目录：便携模式优先，否则 %LOCALAPPDATA%\GameSaveSync（per-user，不需要管理员）。</summary>
    public static AppPaths CreateDefault() =>
        ResolveDefault(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>可测试的解析入口：只在 <paramref name="baseDirectory"/> 下检查 portable.flag。</summary>
    public static AppPaths ResolveDefault(string baseDirectory, string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        try
        {
            if (File.Exists(Path.Combine(baseDirectory, PortableFlagName)))
            {
                return new AppPaths(Path.Combine(baseDirectory, "data"), isPortable: true);
            }
        }
        catch
        {
            // 程序目录不可读时退回 per-user 目录
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var root = Path.Combine(localAppData, AppName);
        MigrateLegacyDirectory(Path.Combine(localAppData, LegacyAppName), root);
        return new AppPaths(root);
    }

    /// <summary>
    /// 一次性迁移：老版本数据在 %LOCALAPPDATA%\GameSaveSync，新版本读 %LOCALAPPDATA%\Reload。
    /// 仅当新目录不存在、旧目录存在时整体搬移（原子性靠同卷 rename）。
    /// </summary>
    private static void MigrateLegacyDirectory(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot) || Directory.Exists(newRoot))
            {
                return;
            }

            Directory.Move(legacyRoot, newRoot);
        }
        catch
        {
            // 迁移失败不阻塞启动：新目录会重新创建，旧目录里的数据仍完好，用户可手动搬。
        }
    }
}
