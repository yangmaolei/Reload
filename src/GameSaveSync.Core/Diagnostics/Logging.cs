namespace GameSaveSync.Core.Diagnostics;

public enum AppLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

public sealed record AppLogEntry(DateTimeOffset Timestamp, AppLogLevel Level, string Message, string? Exception);

/// <summary>UI 日志页 / 诊断用的轻量日志抽象（实现可接 Serilog，Core 不依赖 Serilog）。</summary>
public interface IAppLogger
{
    void Log(AppLogLevel level, string message, Exception? exception = null);

    IReadOnlyList<AppLogEntry> Recent(int count);

    event Action<AppLogEntry>? EntryWritten;
}

public static class AppLoggerExtensions
{
    public static void Debug(this IAppLogger logger, string message) =>
        logger.Log(AppLogLevel.Debug, message);

    public static void Info(this IAppLogger logger, string message) =>
        logger.Log(AppLogLevel.Information, message);

    public static void Warn(this IAppLogger logger, string message, Exception? exception = null) =>
        logger.Log(AppLogLevel.Warning, message, exception);

    public static void Error(this IAppLogger logger, string message, Exception? exception = null) =>
        logger.Log(AppLogLevel.Error, message, exception);
}

/// <summary>固定容量环形缓冲（默认 200 条，与规格的诊断页一致）。</summary>
public sealed class InMemoryAppLogger : IAppLogger
{
    private readonly object _gate = new();
    private readonly Queue<AppLogEntry> _entries;

    public InMemoryAppLogger(int capacity = 200)
    {
        Capacity = Math.Max(1, capacity);
        _entries = new Queue<AppLogEntry>(Capacity);
    }

    public int Capacity { get; }

    public event Action<AppLogEntry>? EntryWritten;

    public void Log(AppLogLevel level, string message, Exception? exception = null)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, message, exception?.ToString());
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        EntryWritten?.Invoke(entry);
    }

    public IReadOnlyList<AppLogEntry> Recent(int count)
    {
        lock (_gate)
        {
            if (count <= 0 || count >= _entries.Count)
            {
                return _entries.ToArray();
            }

            return _entries.Skip(_entries.Count - count).ToArray();
        }
    }
}

/// <summary>可测试的时间源。</summary>
public interface IClock
{
    long UtcNowMs { get; }
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DateTimeOffset Now => DateTimeOffset.Now;
}

public static class TimeUtil
{
    public static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static string FormatLocal(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>快照文件名前缀：本地时间 yyyyMMdd-HHmmss，便于人眼排序。</summary>
    public static string SnapshotFilePrefix(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyyMMdd-HHmmss");
}
