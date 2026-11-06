using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.App.Services;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;

namespace GameSaveSync.App.ViewModels;

/// <summary>诊断页：存储/队列快照、最近日志、连接测试、脱敏诊断包导出。</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsService _diagnostics;
    private readonly WebDavSettingsService _webDav;
    private readonly IDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;

    public DiagnosticsViewModel(
        DiagnosticsService diagnostics,
        WebDavSettingsService webDav,
        IDialogService dialogs,
        IAppLogger logger,
        AppPaths paths)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _webDav = webDav ?? throw new ArgumentNullException(nameof(webDav));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        DataRootText = paths.DataRoot;
    }

    public ObservableCollection<OutboxDiagnostic> Outbox { get; } = new();

    public ObservableCollection<LogEntryViewModel> Logs { get; } = new();

    [ObservableProperty]
    private DiagnosticsReport? report;

    [ObservableProperty]
    private string statusText = "点「刷新」加载当前状态";

    [ObservableProperty]
    private string outboxSummary = "—";

    [ObservableProperty]
    private bool isBusy;

    public string DataRootText { get; }

    public string RemoteText => Report is null
        ? "—"
        : Report.RemoteConfigured
            ? $"{Report.RemoteUrl} / {Report.RemoteRoot}"
            : "未配置（快照只保存在本机）";

    public string PortableText => _paths.IsPortable ? "便携模式" : "标准安装";

    partial void OnReportChanged(DiagnosticsReport? value)
    {
        OnPropertyChanged(nameof(RemoteText));

        Outbox.Clear();
        if (value is not null)
        {
            foreach (var item in value.RecentOutbox)
            {
                Outbox.Add(item);
            }
        }

        Logs.Clear();
        if (value is not null)
        {
            foreach (var entry in value.RecentLogs.Reverse())
            {
                Logs.Add(new LogEntryViewModel(entry));
            }
        }

        OutboxSummary = value is null || value.OutboxCounts.Count == 0
            ? "队列为空"
            : string.Join("   ·   ", value.OutboxCounts
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key} {kv.Value}"));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Report = await _diagnostics.BuildAsync(CancellationToken.None).ConfigureAwait(true);
            StatusText = $"已刷新（{DateTime.Now:HH:mm:ss}）";
        }
        catch (Exception ex)
        {
            _logger.Error("加载诊断信息失败", ex);
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Report is null)
        {
            await LoadAsync().ConfigureAwait(true);
        }

        if (Report is null)
        {
            return;
        }

        var fileName = $"Reload-诊断-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var target = _dialogs.PickSaveFile(fileName);
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var report = Report;
            await Task.Run(() => _diagnostics.Export(report, target)).ConfigureAwait(true);
            StatusText = $"诊断包已导出：{target}（已脱敏）";
        }
        catch (Exception ex)
        {
            _logger.Error("导出诊断包失败", ex);
            _dialogs.ShowError($"导出失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在测试连接…";
        try
        {
            var options = await _webDav.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            if (!options.IsConfigured)
            {
                StatusText = "还没有配置 WebDAV 地址";
                return;
            }

            var result = await _webDav.TestAsync(options, null, CancellationToken.None).ConfigureAwait(true);
            StatusText = result.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"测试失败：{ex.Message}";
            _logger.Warn("诊断页连接测试异常", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _paths.DataRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.ShowError($"打开目录失败：{ex.Message}");
        }
    }
}
