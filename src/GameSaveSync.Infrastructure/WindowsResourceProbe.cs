using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Scheduling;

namespace GameSaveSync.Infrastructure;

/// <summary>
/// Windows 资源探针：
/// <list type="bullet">
/// <item>CPU：<c>GetSystemTimes</c> 两次采样求差（不依赖本地化的性能计数器）。</item>
/// <item>磁盘：<c>PhysicalDisk\Avg. Disk Queue Length</c>，计数器不可用时保守返回"忙"以外的 false 并只警告一次。</item>
/// <item>全屏独占：<c>SHQueryUserNotificationState</c>（QUNS_BUSY / D3D_FULL_SCREEN / PRESENTATION_MODE）。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsResourceProbe : IResourceProbe, IDisposable
{
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    private readonly IAppLogger _logger;
    private readonly TimeSpan _cpuSampleWindow;
    private readonly double _diskQueueThreshold;
    private PerformanceCounter? _diskQueue;
    private bool _diskCounterUnavailable;

    public WindowsResourceProbe(
        IAppLogger logger,
        TimeSpan? cpuSampleWindow = null,
        double diskQueueThreshold = 1.5)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cpuSampleWindow = cpuSampleWindow ?? TimeSpan.FromMilliseconds(250);
        _diskQueueThreshold = diskQueueThreshold;
    }

    public async Task<double> GetCpuUsageAsync(CancellationToken ct)
    {
        if (!GetSystemTimes(out var idleBefore, out var kernelBefore, out var userBefore))
        {
            return 100;   // 取不到就当作"忙"，宁可不做 idle 快照
        }

        await Task.Delay(_cpuSampleWindow, ct).ConfigureAwait(false);

        if (!GetSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter))
        {
            return 100;
        }

        var idle = ToUInt64(idleAfter) - ToUInt64(idleBefore);
        var kernel = ToUInt64(kernelAfter) - ToUInt64(kernelBefore);
        var user = ToUInt64(userAfter) - ToUInt64(userBefore);

        var total = kernel + user;   // kernel 已包含 idle
        if (total == 0)
        {
            return 0;
        }

        var busy = total > idle ? total - idle : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    public Task<bool> IsDiskBusyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_diskCounterUnavailable)
        {
            return Task.FromResult(false);
        }

        try
        {
            _diskQueue ??= new PerformanceCounter("PhysicalDisk", "Avg. Disk Queue Length", "_Total", readOnly: true);
            var value = _diskQueue.NextValue();

            // 第一次读取恒为 0，多读一次让计数器稳定
            if (value <= 0)
            {
                Thread.Sleep(60);
                value = _diskQueue.NextValue();
            }

            return Task.FromResult(value > _diskQueueThreshold);
        }
        catch (Exception ex)
        {
            _diskCounterUnavailable = true;
            _logger.Warn("磁盘队列计数器不可用，idle 快照将只依据 CPU 与全屏状态", ex);
            return Task.FromResult(false);
        }
    }

    public bool IsFullScreenExclusive()
    {
        try
        {
            var state = SHQueryUserNotificationState(out var value);
            if (state != 0)
            {
                return false;
            }

            return value is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode;
        }
        catch
        {
            return false;
        }
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public void Dispose() => _diskQueue?.Dispose();
}

/// <summary>
/// 后台 IO 作用域：把当前线程优先级降到 IDLE，退出时恢复。
/// 规格要求"任何情况下不得抢占游戏 IO"，线程优先级比进程优先级更精准。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ThreadBackgroundIoScope : IBackgroundIoScope
{
    private const int ThreadPriorityIdle = -15;
    private const int ThreadPriorityNormal = 0;

    public IDisposable Enter() => new Scope();

    private sealed class Scope : IDisposable
    {
        private readonly bool _changed;

        public Scope()
        {
            try
            {
                _changed = SetThreadPriority(GetCurrentThread(), ThreadPriorityIdle);
            }
            catch
            {
                _changed = false;
            }
        }

        public void Dispose()
        {
            if (!_changed)
            {
                return;
            }

            try
            {
                SetThreadPriority(GetCurrentThread(), ThreadPriorityNormal);
            }
            catch
            {
                // 恢复失败不影响正确性
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
}
