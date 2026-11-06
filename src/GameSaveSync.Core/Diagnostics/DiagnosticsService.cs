using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameSaveSync.Core.Abstractions;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Sync;

namespace GameSaveSync.Core.Diagnostics;

public sealed record OutboxDiagnostic(string Op, string State, int Attempts, string? LastError, long? NextRetryAt);

public sealed record StorageDiagnostic(
    int Games,
    int Snapshots,
    int Objects,
    long ObjectBytes,
    long DatabaseBytes,
    int PendingSnapshots);

public sealed record DiagnosticsReport(
    string AppVersion,
    DateTimeOffset GeneratedAt,
    string DataRoot,
    bool Portable,
    string DeviceId,
    string DeviceName,
    bool RemoteConfigured,
    string? RemoteUrl,
    string? RemoteRoot,
    StorageDiagnostic Storage,
    IReadOnlyDictionary<string, int> OutboxCounts,
    IReadOnlyList<OutboxDiagnostic> RecentOutbox,
    IReadOnlyList<AppLogEntry> RecentLogs,
    IReadOnlyList<string> Notes)
{
    /// <summary>纯文本摘要（导出包里的 report.txt）。调用方负责先脱敏。</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Reload 诊断报告");
        builder.AppendLine("=====================");
        builder.AppendLine($"生成时间   : {GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"版本       : {AppVersion}");
        builder.AppendLine($"数据目录   : {DataRoot}{(Portable ? "（便携模式）" : string.Empty)}");
        builder.AppendLine($"设备       : {DeviceName} / {DeviceId}");
        builder.AppendLine();
        builder.AppendLine("远端");
        builder.AppendLine($"  已配置   : {(RemoteConfigured ? "是" : "否")}");
        builder.AppendLine($"  地址     : {RemoteUrl ?? "-"}");
        builder.AppendLine($"  根目录   : {RemoteRoot ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("本地存储");
        builder.AppendLine($"  游戏     : {Storage.Games}");
        builder.AppendLine($"  快照     : {Storage.Snapshots}（待上传 {Storage.PendingSnapshots}）");
        builder.AppendLine($"  对象     : {Storage.Objects} 个 / {FormatBytes(Storage.ObjectBytes)}");
        builder.AppendLine($"  数据库   : {FormatBytes(Storage.DatabaseBytes)}");
        builder.AppendLine();
        builder.AppendLine("上传队列");
        if (OutboxCounts.Count == 0)
        {
            builder.AppendLine("  （空）");
        }
        else
        {
            foreach (var (state, count) in OutboxCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"  {state,-10}: {count}");
            }
        }

        if (RecentOutbox.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("最近待办");
            foreach (var item in RecentOutbox)
            {
                builder.AppendLine(
                    $"  [{item.State}] {item.Op} attempts={item.Attempts} " +
                    (item.LastError is null ? string.Empty : $"err={item.LastError}"));
            }
        }

        if (Notes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("说明");
            foreach (var note in Notes)
            {
                builder.AppendLine($"  - {note}");
            }
        }

        return builder.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>
/// 诊断：把队列、存储、日志、远端配置汇总成一份可导出的包（默认脱敏）。
/// 绝不包含密码、DPAPI 密文、完整用户名路径。
/// </summary>
public sealed class DiagnosticsService
{
    private readonly SqliteDatabase _database;
    private readonly IObjectRepository _objects;
    private readonly ISnapshotRepository _snapshots;
    private readonly IOutboxStore _outbox;
    private readonly IRemoteStore _remote;
    private readonly ISettingsRepository _settings;
    private readonly IAppLogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly string _appVersion;

    public DiagnosticsService(
        SqliteDatabase database,
        IObjectRepository objects,
        ISnapshotRepository snapshots,
        IOutboxStore outbox,
        IRemoteStore remote,
        ISettingsRepository settings,
        IAppLogger logger,
        IFileSystem fileSystem,
        AppPaths paths,
        IClock clock,
        string? appVersion = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _appVersion = appVersion ?? typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public async Task<DiagnosticsReport> BuildAsync(CancellationToken ct)
    {
        var counts = await _database.WithConnectionAsync(async (connection, token) =>
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            async Task<int> ScalarAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }

            result["games"] = await ScalarAsync("SELECT COUNT(1) FROM games").ConfigureAwait(false);
            result["snapshots"] = await ScalarAsync(
                "SELECT COUNT(1) FROM snapshots WHERE local_state = 'present'").ConfigureAwait(false);
            result["pendingSnapshots"] = await ScalarAsync(
                "SELECT COUNT(1) FROM snapshots WHERE remote_state = 'pending' AND local_state = 'present'")
                .ConfigureAwait(false);

            await using var outboxCommand = connection.CreateCommand();
            outboxCommand.CommandText = "SELECT state, COUNT(1) FROM outbox GROUP BY state";
            await using var reader = await outboxCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                result[$"outbox.{reader.GetString(0)}"] = reader.GetInt32(1);
            }

            return result;
        }, ct).ConfigureAwait(false);

        var objectCount = await _database.WithConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM objects";
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }, ct).ConfigureAwait(false);

        var objectBytes = await _objects.TotalStoredBytesAsync(ct).ConfigureAwait(false);
        var dbBytes = SafeFileSize(_paths.DatabasePath);
        var remoteUrl = await _settings.GetAsync(SettingsRepository.WebDavUrlKey, ct).ConfigureAwait(false);
        var remoteRoot = await _settings.GetAsync(SettingsRepository.WebDavRootKey, ct).ConfigureAwait(false);
        var deviceId = await _settings.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var deviceName = await _settings.GetAsync(SettingsRepository.DeviceNameKey, ct).ConfigureAwait(false)
                         ?? Environment.MachineName;

        var outboxCounts = counts
            .Where(kv => kv.Key.StartsWith("outbox.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kv => kv.Key["outbox.".Length..], kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var recentOutbox = (await _outbox.ListRecentAsync(20, ct).ConfigureAwait(false))
            .Select(item => new OutboxDiagnostic(item.Op, item.State, item.Attempts, item.LastError, item.NextRetryAt))
            .ToList();

        var notes = new List<string>();
        if (!_remote.IsConfigured)
        {
            notes.Add("远端未配置：快照只保存在本机");
        }

        if (_paths.IsPortable)
        {
            notes.Add("便携模式：数据目录在程序同级 data\\，不写注册表");
        }

        if (counts.TryGetValue("pendingSnapshots", out var pending) && pending > 0)
        {
            notes.Add($"{pending} 个快照等待上传（断网或服务端问题，恢复后会自动重试）");
        }

        return new DiagnosticsReport(
            _appVersion,
            DateTimeOffset.Now,
            _paths.DataRoot,
            _paths.IsPortable,
            deviceId,
            deviceName,
            _remote.IsConfigured,
            remoteUrl,
            remoteRoot,
            new StorageDiagnostic(
                counts.GetValueOrDefault("games"),
                counts.GetValueOrDefault("snapshots"),
                objectCount,
                objectBytes,
                dbBytes,
                counts.GetValueOrDefault("pendingSnapshots")),
            outboxCounts,
            recentOutbox,
            _logger.Recent(200),
            notes);
    }

    /// <summary>导出诊断包（zip）：report.txt / logs.txt / diagnostics.json / README.txt，全部脱敏。</summary>
    public string Export(DiagnosticsReport report, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var redactor = DiagnosticRedactor.CreateDefault(
            _paths,
            new[] { _environmentUserName(), report.DeviceName },
            extraSecrets: Array.Empty<string>());

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "report.txt", redactor.Redact(report.ToText()));
        WriteEntry(archive, "logs.txt", redactor.Redact(FormatLogs(report.RecentLogs)));
        WriteEntry(archive, "diagnostics.json", redactor.Redact(JsonSerializer.Serialize(report, JsonOptions)));
        WriteEntry(archive, "README.txt",
            "Reload 诊断包\r\n\r\n" +
            "report.txt      汇总（存储、队列、远端配置）\r\n" +
            "logs.txt        最近 200 条日志\r\n" +
            "diagnostics.json 原始结构，便于排查\r\n\r\n" +
            "已自动脱敏：用户名、用户目录、数据目录、URL 里的凭据、密码字段。\r\n" +
            "不包含：WebDAV 密码、DPAPI 密文、存档内容、证书私钥。\r\n");

        _logger.Info($"已导出诊断包：{targetPath}");
        return targetPath;
    }

    private long SafeFileSize(string path)
    {
        try
        {
            return _fileSystem.FileExists(path) ? _fileSystem.Stat(path).Size : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatLogs(IReadOnlyList<AppLogEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [")
                .Append(entry.Level.ToString().ToUpperInvariant())
                .Append("] ")
                .AppendLine(entry.Message);

            if (!string.IsNullOrWhiteSpace(entry.Exception))
            {
                builder.AppendLine(entry.Exception);
            }
        }

        return builder.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string _environmentUserName() =>
        Environment.UserName ?? string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// 脱敏器：把用户名、用户目录、数据目录、URL 凭据、password= 之类替换成占位符。
/// 规则集中在这里，方便单测"给定输入必须不含敏感串"。
/// </summary>
public sealed class DiagnosticRedactor
{
    private readonly List<(string Value, string Replacement)> _replacements = new();

    public DiagnosticRedactor(IEnumerable<(string Value, string Replacement)> replacements)
    {
        foreach (var (value, replacement) in replacements)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            {
                _replacements.Add((value, replacement));
            }
        }

        // 长值优先，避免 "C:\Users\yang" 先被 "C:\Users" 吃掉
        _replacements.Sort((left, right) => right.Value.Length.CompareTo(left.Value.Length));
    }

    public static DiagnosticRedactor CreateDefault(
        AppPaths paths,
        IEnumerable<string> additionalSensitiveValues,
        IEnumerable<string> extraSecrets)
    {
        var replacements = new List<(string, string)>
        {
            (paths.DataRoot, "<DATA>"),
        };

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            replacements.Add((profile, "%USERPROFILE%"));
        }

        foreach (var value in additionalSensitiveValues.Concat(extraSecrets))
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            {
                replacements.Add((value, "<redacted>"));
            }
        }

        return new DiagnosticRedactor(replacements);
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = input;
        foreach (var (value, replacement) in _replacements)
        {
            result = result.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
        }

        // https://user:pass@host → https://<redacted>@host
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?<scheme>https?://)[^/\s:@]+:[^/\s@]+@",
            "${scheme}<redacted>@",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Authorization: Basic xxx
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?<key>authorization\s*:\s*)\S+",
            "${key}<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // password=xxx / pwd: xxx
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?<key>\b(?:password|passwd|pwd|secret|token)\b\s*[=:]\s*)\S+",
            "${key}<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return result;
    }
}
