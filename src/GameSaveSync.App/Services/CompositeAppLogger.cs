using GameSaveSync.Core.Diagnostics;
using Serilog;

namespace GameSaveSync.App.Services;

/// <summary>
/// 日志双写：一份进 Serilog（滚动文件，供排查），一份进内存环形缓冲（供 UI 日志页）。
/// 凭据绝不出现在日志里（调用方负责不把密码传进来）。
/// </summary>
public sealed class CompositeAppLogger : IAppLogger
{
    private readonly InMemoryAppLogger _memory = new(200);
    private readonly ILogger _serilog;

    public CompositeAppLogger(ILogger serilog) => _serilog = serilog ?? throw new ArgumentNullException(nameof(serilog));

    public event Action<AppLogEntry>? EntryWritten
    {
        add => _memory.EntryWritten += value;
        remove => _memory.EntryWritten -= value;
    }

    public void Log(AppLogLevel level, string message, Exception? exception = null)
    {
        _memory.Log(level, message, exception);

        switch (level)
        {
            case AppLogLevel.Debug:
                _serilog.Debug(exception, message);
                break;
            case AppLogLevel.Information:
                _serilog.Information(exception, message);
                break;
            case AppLogLevel.Warning:
                _serilog.Warning(exception, message);
                break;
            default:
                _serilog.Error(exception, message);
                break;
        }
    }

    public IReadOnlyList<AppLogEntry> Recent(int count) => _memory.Recent(count);
}
