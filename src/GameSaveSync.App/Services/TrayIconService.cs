using System.Drawing;
using System.Windows.Forms;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Scheduling;
using GameSaveSync.Infrastructure;

namespace GameSaveSync.App.Services;

/// <summary>
/// 托盘常驻：状态图标 + 暂停 30min / 1h / 直到重启 + 开机自启 + 退出。
/// 关窗只是隐藏，真正退出必须走托盘菜单（M2 的"关窗 = 最小化到托盘"）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>加载嵌入的应用图标；失败退回系统默认（托盘仍然可用）。</summary>
    internal static System.Drawing.Icon LoadAppIcon(int size = 16)
    {
        try
        {
            var assembly = typeof(TrayIconService).Assembly;
            using var stream = assembly.GetManifestResourceStream("Reload.Assets.app.ico");
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream, size, size);
            }
        }
        catch
        {
        }

        return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "Reload.exe")
               ?? System.Drawing.SystemIcons.Application;
    }


    private readonly SnapshotScheduler _scheduler;
    private readonly AutoStartService _autoStart;
    private readonly IAppLogger _logger;
    private readonly Action _showWindow;
    private readonly Action _exitApplication;

    private NotifyIcon? _icon;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _autoStartItem;
    private ToolStripMenuItem? _resumeItem;

    public TrayIconService(
        SnapshotScheduler scheduler,
        AutoStartService autoStart,
        IAppLogger logger,
        Action showWindow,
        Action exitApplication)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _autoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
    }

    public void Initialize()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("状态：自动备份运行中") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开主窗口", null, (_, _) => _showWindow()));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("暂停 30 分钟", null, (_, _) => Pause(TimeSpan.FromMinutes(30))));
        menu.Items.Add(new ToolStripMenuItem("暂停 1 小时", null, (_, _) => Pause(TimeSpan.FromHours(1))));
        menu.Items.Add(new ToolStripMenuItem("暂停直到重启", null, (_, _) => Pause(null)));

        _resumeItem = new ToolStripMenuItem("恢复自动备份", null, (_, _) => Resume());
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());

        _autoStartItem = new ToolStripMenuItem("开机自启", null, (_, _) => ToggleAutoStart())
        {
            Checked = _autoStart.IsEnabled,
            CheckOnClick = false,
        };
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => _exitApplication()));

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Reload — 自动备份已启用",
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => _showWindow();
        _icon.BalloonTipClicked += (_, _) => _showWindow();
    }

    public void UpdateStatus(string text, bool isError = false)
    {
        if (_statusItem is not null)
        {
            _statusItem.Text = $"状态：{text}";
        }

        if (_icon is not null)
        {
            _icon.Text = Trim(text);
            _icon.Icon = LoadAppIcon();
        }
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _icon?.ShowBalloonTip(4000, title, text, icon);
    }

    public void ShowMinimizedHint()
    {
        ShowBalloon("Reload", "已最小化到托盘，自动备份继续在后台运行。双击图标可重新打开。");
    }

    private void Pause(TimeSpan? duration)
    {
        _scheduler.Pause(duration);
        var label = duration is null ? "直到重启" : $"{duration.Value.TotalMinutes:0} 分钟";
        UpdateStatus($"已暂停（{label}）");
        _logger.Info($"托盘：暂停自动备份（{label}）");
    }

    private void Resume()
    {
        _scheduler.Resume();
        UpdateStatus("自动备份运行中");
        _logger.Info("托盘：恢复自动备份");
    }

    private void ToggleAutoStart()
    {
        if (_autoStartItem is null)
        {
            return;
        }

        var next = !_autoStart.IsEnabled;
        var ok = _autoStart.TrySet(next);

        // 写入后重新检测：登记成功不等于真的能开机启动
        var status = _autoStart.GetStatus();
        _autoStartItem.Checked = status.IsConfigured;

        _logger.Info(ok
            ? $"托盘：开机自启已{(next ? "开启" : "关闭")}（{status.Detail}）"
            : "托盘：开机自启设置失败（可能被组策略限制）");

        if (!ok || (next && !status.IsEffective))
        {
            ShowBalloon(
                "开机自启",
                ok
                    ? $"已登记但未生效：{status.Detail}"
                    : "设置失败，可能被组策略或安全软件阻止。",
                ToolTipIcon.Warning);
        }
    }

    private static string Trim(string text) => text.Length <= 63 ? text : text[..60] + "…";

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }
    }
}
