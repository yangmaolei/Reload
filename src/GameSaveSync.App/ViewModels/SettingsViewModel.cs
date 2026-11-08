using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.App.Services;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Snapshots;
using GameSaveSync.Core.Storage;
using GameSaveSync.Core.Theming;
using GameSaveSync.Infrastructure;
using GameSaveSync.WebDav;

namespace GameSaveSync.App.ViewModels;

/// <summary>自启动方式的一个选项（方式 + 显示名）。</summary>
public sealed record AutoStartOption(AutoStartMethod Method, string DisplayName);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly WebDavSettingsService _webDav;
    private readonly ISettingsRepository _settings;
    private readonly IDeviceRepository _devices;
    private readonly IRemoteIndexService _remoteIndex;
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly AutoStartService _autoStart;
    private readonly ThemeService _theme;
    private WebDavOptions _loaded = new();
    private bool _loading;

    public SettingsViewModel(
        WebDavSettingsService webDav,
        ISettingsRepository settings,
        IDeviceRepository devices,
        IRemoteIndexService remoteIndex,
        AppPaths paths,
        IAppLogger logger,
        AutoStartService autoStart,
        ThemeService theme)
    {
        _webDav = webDav ?? throw new ArgumentNullException(nameof(webDav));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _remoteIndex = remoteIndex ?? throw new ArgumentNullException(nameof(remoteIndex));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _autoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    /// <summary>由 PasswordBox 直接写入，绝不参与双向绑定（避免明文进可视树）。</summary>
    public string? PasswordInput { get; set; }

    [ObservableProperty]
    private string baseUrl = string.Empty;

    [ObservableProperty]
    private string username = string.Empty;

    [ObservableProperty]
    private string remoteRoot = AppPaths.DefaultRemoteRoot;

    [ObservableProperty]
    private string certificateThumbprint = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string deviceIdText = string.Empty;

    [ObservableProperty]
    private string dataRootText = string.Empty;

    [ObservableProperty]
    private bool hasStoredPassword;

    [ObservableProperty]
    private string devicesText = string.Empty;

    [ObservableProperty]
    private bool autoStartEnabled;

    [ObservableProperty]
    private bool autoStartAvailable;

    /// <summary>自启动检测结果的人类可读说明（是否真实生效、为什么）。</summary>
    [ObservableProperty]
    private string autoStartStatusText = string.Empty;

    /// <summary>自启动方式（0=关闭，1=注册表，2=启动文件夹）。</summary>
    [ObservableProperty]
    private int autoStartMethodIndex;

    /// <summary>自启动方式的可选项，供设置页下拉绑定。</summary>
    public IReadOnlyList<AutoStartOption> AutoStartMethods { get; } = new[]
    {
        new AutoStartOption(AutoStartMethod.None, "关闭"),
        new AutoStartOption(AutoStartMethod.Registry, "注册表启动项（HKCU\\Run）"),
        new AutoStartOption(AutoStartMethod.StartupFolder, "启动文件夹快捷方式"),
    };

    partial void OnAutoStartMethodIndexChanged(int value)
    {
        if (_loading || _paths.IsPortable)
        {
            return;
        }

        ApplyAutoStart((AutoStartMethod)Math.Clamp(value, 0, 2));
    }

    [ObservableProperty]
    private string appVersionText = string.Empty;

    [ObservableProperty]
    private string portableText = string.Empty;

    [ObservableProperty]
    private string rulesPathText = string.Empty;

    // ===== 外观 =====

    /// <summary>预设主题色（名称 + 预览色值），供设置页色板绑定。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ThemeSwatch> ThemeSwatches { get; } = new()
    {
        new ThemeSwatch(ThemeSeed.Teal, "青", "#00696D", "#4CD9E0"),
        new ThemeSwatch(ThemeSeed.Indigo, "靛蓝", "#3949AB", "#BAC3FF"),
        new ThemeSwatch(ThemeSeed.Emerald, "翠绿", "#006E1C", "#7ADB84"),
        new ThemeSwatch(ThemeSeed.Amber, "琥珀", "#7E5700", "#FFB949"),
        new ThemeSwatch(ThemeSeed.Rose, "玫红", "#A61E4D", "#FFB1C0"),
        new ThemeSwatch(ThemeSeed.Slate, "石墨", "#454E5C", "#B4C4DF"),
    };

    public IReadOnlyList<string> ThemeModes { get; } = new[] { "跟随系统", "亮色", "暗色" };

    [ObservableProperty]
    private int selectedSeedIndex;

    [ObservableProperty]
    private int selectedModeIndex;

    partial void OnSelectedSeedIndexChanged(int value) => SyncSwatchSelection();

    private void SyncSwatchSelection()
    {
        for (var i = 0; i < ThemeSwatches.Count; i++)
        {
            ThemeSwatches[i].IsSelected = i == SelectedSeedIndex;
        }
    }

    // ===== 自动化参数 =====

    [ObservableProperty]
    private string exitDelayText = "2";

    [ObservableProperty]
    private string debounceText = "3";

    [ObservableProperty]
    private string idleIntervalText = "15";

    // ===== 全局排除 =====

    [ObservableProperty]
    private string globalExcludesText = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        _loaded = await _webDav.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        BaseUrl = _loaded.BaseUrl;
        Username = _loaded.Username ?? string.Empty;
        RemoteRoot = _loaded.RemoteRoot;
        CertificateThumbprint = _loaded.TrustedCertificateThumbprint ?? string.Empty;
        HasStoredPassword = !string.IsNullOrEmpty(_loaded.Password);
        DeviceIdText = await _settings.GetDeviceIdAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshDevicesAsync().ConfigureAwait(true);
        DataRootText = _paths.DataRoot;
        StatusText = _loaded.IsConfigured ? "已配置" : "尚未配置 WebDAV（快照只会保存在本地）";

        PortableText = _paths.IsPortable
            ? "便携模式：数据在程序目录 data\\，不写注册表"
            : "标准模式：数据在 %LOCALAPPDATA%\\Reload";
        RulesPathText = System.IO.Path.Combine(_paths.DataRoot, "save-paths.yaml");
        AppVersionText = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        _loading = true;
        AutoStartAvailable = !_paths.IsPortable;

        // 自启动：读取真实状态（不只是"有没有键"，还要看路径是否还有效）
        var autoStartStatus = _paths.IsPortable ? AutoStartStatus.Disabled : _autoStart.GetStatus();
        AutoStartEnabled = autoStartStatus.IsConfigured;
        AutoStartMethodIndex = (int)autoStartStatus.Method;
        AutoStartStatusText = _paths.IsPortable
            ? "便携模式下不写注册表，自启动不可用"
            : autoStartStatus.Detail;
        _loading = false;

        // 外观与高级参数（读不到用默认）
        SelectedSeedIndex = (int)_theme.Seed;
        SelectedModeIndex = (int)_theme.Mode;
        SyncSwatchSelection();

        var exitMs = await _settings.GetAsync("scheduler.exit_delay_ms", CancellationToken.None).ConfigureAwait(true);
        ExitDelayText = (int.TryParse(exitMs, out var e) ? e : 2000) / 1000.0 + "";
        var debounceMs = await _settings.GetAsync("scheduler.debounce_ms", CancellationToken.None).ConfigureAwait(true);
        DebounceText = (int.TryParse(debounceMs, out var d) ? d : 3000) / 1000.0 + "";
        var idleMin = await _settings.GetAsync("scheduler.idle_min", CancellationToken.None).ConfigureAwait(true);
        IdleIntervalText = int.TryParse(idleMin, out var i) ? i + "" : "15";

        var excludes = await _settings.GetAsync(SnapshotService.GlobalExcludeKey, CancellationToken.None).ConfigureAwait(true);
        GlobalExcludesText = excludes ?? string.Empty;
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        if (_loading || _paths.IsPortable)
        {
            return;
        }

        // 旧的开关式入口（保留兼容）：开 = 用当前方式，关 = 关闭
        var method = value
            ? (AutoStartMethod)Math.Clamp(AutoStartMethodIndex, 1, 2)
            : AutoStartMethod.None;

        ApplyAutoStart(method);
    }

    /// <summary>
    /// 应用自启动设置。写入后【重新检测】并把真实状态反馈给用户 ——
    /// 只报告"写入成功"是不够的，登记项可能指向了别的副本。
    /// </summary>
    private void ApplyAutoStart(AutoStartMethod method)
    {
        if (_paths.IsPortable)
        {
            AutoStartStatusText = "便携模式下不写注册表，自启动不可用";
            return;
        }

        var ok = _autoStart.TrySet(method);
        if (!ok)
        {
            AutoStartStatusText = "写入失败（可能被安全软件或组策略拦截）";
            StatusText = AutoStartStatusText;
            _logger.Warn($"设置开机自启失败：{method}");
            return;
        }

        var status = _autoStart.GetStatus();
        AutoStartEnabled = status.IsConfigured;
        AutoStartStatusText = status.Detail;
        StatusText = method == AutoStartMethod.None
            ? "已关闭开机自启"
            : status.IsEffective
                ? "开机自启已生效"
                : $"已登记但未生效：{status.Detail}";

        _logger.Info($"开机自启：方式={method}，状态={status.Detail}");
    }

    /// <summary>重新检测自启动状态（用户在设置页手动刷新）。</summary>
    [RelayCommand]
    private void DetectAutoStart()
    {
        if (_paths.IsPortable)
        {
            AutoStartStatusText = "便携模式下不写注册表，自启动不可用";
            return;
        }

        var status = _autoStart.GetStatus();
        AutoStartEnabled = status.IsConfigured;
        AutoStartStatusText = status.Detail;
        StatusText = status.Detail;
    }

    [RelayCommand]
    private async Task SelectSeedAsync(int index)
    {
        if (index < 0 || index >= ThemeSwatches.Count)
        {
            return;
        }

        SelectedSeedIndex = index;
        await _theme.SetAsync(ThemeSwatches[index].Seed, (ThemeMode)SelectedModeIndex).ConfigureAwait(true);
        StatusText = $"主题已切换：{ThemeSwatches[index].Name}";
    }

    [RelayCommand]
    private async Task SelectModeAsync(int index)
    {
        if (index < 0 || index > 2)
        {
            return;
        }

        SelectedModeIndex = index;
        await _theme.SetAsync(ThemeSwatches[Math.Clamp(SelectedSeedIndex, 0, ThemeSwatches.Count - 1)].Seed,
            (ThemeMode)index).ConfigureAwait(true);
        StatusText = $"外观模式：{ThemeModes[index]}";
    }

    /// <summary>保存自动化参数（写 settings；调度器启动时读取，重启后生效）。</summary>
    [RelayCommand]
    private async Task SaveAutomationAsync()
    {
        var exitSec = Clamp(ExitDelayText, 1, 30, 2);
        var debounceSec = Clamp(DebounceText, 1, 60, 3);
        var idleMin = Clamp(IdleIntervalText, 5, 60, 15);

        await _settings.SetAsync("scheduler.exit_delay_ms", (exitSec * 1000).ToString(), CancellationToken.None).ConfigureAwait(true);
        await _settings.SetAsync("scheduler.debounce_ms", (debounceSec * 1000).ToString(), CancellationToken.None).ConfigureAwait(true);
        await _settings.SetAsync("scheduler.idle_min", idleMin.ToString(), CancellationToken.None).ConfigureAwait(true);

        ExitDelayText = exitSec + "";
        DebounceText = debounceSec + "";
        IdleIntervalText = idleMin + "";
        StatusText = "自动化参数已保存，重启应用后生效";
    }

    /// <summary>保存全局排除（每行一个通配符，下次快照立即生效）。</summary>
    [RelayCommand]
    private async Task SaveGlobalExcludesAsync()
    {
        var lines = GlobalExcludesText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))
            .ToList();

        await _settings.SetAsync(
            SnapshotService.GlobalExcludeKey,
            lines.Count == 0 ? null : string.Join("\n", lines),
            CancellationToken.None).ConfigureAwait(true);

        GlobalExcludesText = string.Join("\n", lines);
        StatusText = lines.Count == 0
            ? "全局排除已清空"
            : $"已保存 {lines.Count} 条全局排除，下次快照生效";
    }

    private static int Clamp(string text, int min, int max, int fallback)
    {
        if (!int.TryParse(text.Trim(), out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.DataRoot,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        var remote = await _remoteIndex.RefreshDevicesAsync(CancellationToken.None).ConfigureAwait(true);
        var localId = await _settings.GetDeviceIdAsync(CancellationToken.None).ConfigureAwait(true);
        var all = await _devices.ListAsync(CancellationToken.None).ConfigureAwait(true);

        DevicesText = all.Count == 0
            ? "（还没有设备记录）"
            : string.Join(Environment.NewLine, all.Select(d =>
                $"{(d.Id == localId ? "★ " : "  ")}{d.Name} · 最后在线 " +
                $"{(d.LastSeenAt > 0 ? TimeUtil.FormatLocal(d.LastSeenAt) : "未知")} · seq {d.Seq}"));

        if (remote.Count > 0)
        {
            StatusText = $"设备列表已刷新（远端 {remote.Count} 台）";
        }
    }

    private WebDavOptions BuildOptions() => new()
    {
        BaseUrl = BaseUrl?.Trim() ?? string.Empty,
        RemoteRoot = string.IsNullOrWhiteSpace(RemoteRoot)
                ? GameSaveSync.Core.Configuration.AppPaths.DefaultRemoteRoot
                : RemoteRoot.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        Password = string.IsNullOrEmpty(PasswordInput) ? _loaded.Password : PasswordInput,
        TrustedCertificateThumbprint = string.IsNullOrWhiteSpace(CertificateThumbprint)
            ? null
            : CertificateThumbprint.Trim(),
        TimeoutSeconds = 60,
    };

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在测试连接…";
        try
        {
            var result = await _webDav.TestAsync(BuildOptions(), PasswordInput, CancellationToken.None)
                .ConfigureAwait(true);
            StatusText = result.Message;
            _logger.Info($"WebDAV 连接测试：{result.Message}");
        }
        catch (Exception ex)
        {
            StatusText = $"测试失败：{ex.Message}";
            _logger.Warn("WebDAV 连接测试异常", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var options = BuildOptions();
        if (!options.IsConfigured)
        {
            StatusText = "服务地址不是合法 URL";
            return;
        }

        IsBusy = true;
        try
        {
            await _webDav.SaveAsync(options, PasswordInput, CancellationToken.None).ConfigureAwait(true);
            _loaded = options;
            HasStoredPassword = !string.IsNullOrEmpty(options.Password);
            StatusText = "已保存（密码用 DPAPI 加密存储）";
            _logger.Info($"WebDAV 配置已保存：{options.BaseUrl} / {options.RemoteRoot}");
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
            _logger.Error("保存 WebDAV 配置失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(AppLogEntry entry) => Entry = entry;

    public AppLogEntry Entry { get; }

    public string Time => Entry.Timestamp.ToString("MM-dd HH:mm:ss");

    public string Level => Entry.Level switch
    {
        AppLogLevel.Debug => "调试",
        AppLogLevel.Information => "信息",
        AppLogLevel.Warning => "警告",
        _ => "错误",
    };

    public string Message => Entry.Exception is null
        ? Entry.Message
        : $"{Entry.Message}{Environment.NewLine}{Entry.Exception}";
}

public sealed partial class LogViewModel : ObservableObject
{
    private readonly IAppLogger _logger;

    public LogViewModel(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.EntryWritten += OnEntryWritten;
    }

    public System.Collections.ObjectModel.ObservableCollection<LogEntryViewModel> Entries { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _logger.Recent(200))
        {
            Entries.Add(new LogEntryViewModel(entry));
        }
    }

    private void OnEntryWritten(AppLogEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Append(entry);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => Append(entry)));
    }

    private void Append(AppLogEntry entry)
    {
        Entries.Insert(0, new LogEntryViewModel(entry));
        while (Entries.Count > 200)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}

/// <summary>主题色板的一项：名称 + 亮/暗两个预览色 + 是否选中。</summary>
public sealed class ThemeSwatch : ObservableObject
{
    public ThemeSwatch(ThemeSeed seed, string name, string lightHex, string darkHex)
    {
        Seed = seed;
        Name = name;
        LightHex = lightHex;
        DarkHex = darkHex;
    }

    public ThemeSeed Seed { get; }

    public string Name { get; }

    public string LightHex { get; }

    public string DarkHex { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
