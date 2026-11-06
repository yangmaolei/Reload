using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSaveSync.App.Services;
using GameSaveSync.Core.Configuration;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Probe;
using GameSaveSync.Core.Storage;
using GameSaveSync.WebDav;

namespace GameSaveSync.App.ViewModels;

/// <summary>扫描结果里的一行：默认勾选"找到存档目录且未添加过"的游戏。</summary>
public sealed partial class DiscoveryItemViewModel : ObservableObject
{
    public DiscoveryItemViewModel(DiscoveredGame model)
    {
        Model = model;
        isSelected = model.SuggestedRoots.Count > 0 && !model.AlreadyAdded;
    }

    public DiscoveredGame Model { get; }

    public string Name => Model.Name;

    public string PlatformText => Model.PlatformLabel;

    public string Detail => Model.InstallDir ?? string.Join("  ·  ", Model.SuggestedRoots.Select(Shorten));

    public string StatusText => Model.AlreadyAdded
        ? "已在库中"
        : Model.SuggestedRoots.Count == 0
            ? "未找到存档目录"
            : $"{Model.SuggestedRoots.Count} 个存档目录";

    public string NotesText => string.Join("；", Model.Notes);

    public bool HasNotes => Model.Notes.Count > 0;

    public bool HasRoots => Model.SuggestedRoots.Count > 0;

    [ObservableProperty]
    private bool isSelected;

    private static string Shorten(string path) => path.Length <= 52 ? path : "…" + path[^52..];
}

/// <summary>
/// 首次启动 / 扫描游戏向导：三步（连接 WebDAV → 扫描游戏 → 确认）。
/// 每步都可以跳过；跳过 WebDAV 时快照只留在本机，之后可在设置里补。
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly GameDiscoveryService _discovery;
    private readonly IGameRepository _games;
    private readonly WebDavSettingsService _webDav;
    private readonly ISettingsRepository _settings;
    private readonly IAppLogger _logger;
    private readonly IClock _clock;
    private readonly AppPaths _paths;
    private WebDavOptions _loaded = new();
    private bool _webDavConfigured;

    public WizardViewModel(
        GameDiscoveryService discovery,
        IGameRepository games,
        WebDavSettingsService webDav,
        ISettingsRepository settings,
        IAppLogger logger,
        IClock clock,
        AppPaths paths)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _webDav = webDav ?? throw new ArgumentNullException(nameof(webDav));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        Discovered.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));
    }

    public event Action? Completed;

    public ObservableCollection<DiscoveryItemViewModel> Discovered { get; } = new();

    public string? PasswordInput { get; set; }

    [ObservableProperty]
    private int stepIndex;

    [ObservableProperty]
    private string baseUrl = string.Empty;

    [ObservableProperty]
    private string username = string.Empty;

    [ObservableProperty]
    private string remoteRoot = AppPaths.DefaultRemoteRoot;

    [ObservableProperty]
    private string certificateThumbprint = string.Empty;

    [ObservableProperty]
    private string webDavStatus = string.Empty;

    [ObservableProperty]
    private string scanStatus = "还没有扫描";

    [ObservableProperty]
    private string finishSummary = string.Empty;

    [ObservableProperty]
    private bool hasStoredPassword;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isScanning;

    public int AddedCount { get; private set; }

    public int SelectedCount => Discovered.Count(item => item.IsSelected);

    public bool HasResults => Discovered.Count > 0;

    public bool CanGoBack => StepIndex > 0 && !IsBusy;

    public bool IsWebDavStep => StepIndex == 0;

    public bool IsScanStep => StepIndex == 1;

    public bool IsConfirmStep => StepIndex == 2;

    public string StepCounterText => $"第 {StepIndex + 1} 步 / 共 3 步";

    public string StepTitle => StepIndex switch
    {
        0 => "连接云端",
        1 => "选择游戏",
        _ => "确认并开始",
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "用一个 WebDAV 目录同步存档。可以稍后再配，先只做本地备份。",
        1 => "从 Steam / Epic / GOG / Ubisoft / Xbox 里找出已安装的游戏，自动匹配常见存档目录。",
        _ => "确认要加入的游戏。加好后可以在详情页调整存档目录或开启探测模式。",
    };

    public string RulesHint => $"自定义规则可放在 {Path.Combine(_paths.DataRoot, "save-paths.yaml")}（同 id 覆盖内置规则）";

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsWebDavStep));
        OnPropertyChanged(nameof(IsScanStep));
        OnPropertyChanged(nameof(IsConfirmStep));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
    }

    public async Task InitializeAsync(int startStep, CancellationToken ct = default)
    {
        _loaded = await _webDav.LoadAsync(ct).ConfigureAwait(true);
        _webDavConfigured = _loaded.IsConfigured;
        BaseUrl = _loaded.BaseUrl;
        Username = _loaded.Username ?? string.Empty;
        RemoteRoot = _loaded.RemoteRoot;
        CertificateThumbprint = _loaded.TrustedCertificateThumbprint ?? string.Empty;
        HasStoredPassword = !string.IsNullOrEmpty(_loaded.Password);
        WebDavStatus = _webDavConfigured ? "已配置，可直接继续" : "未配置：可以跳过，之后在设置里补";

        StepIndex = startStep <= 0 || !_webDavConfigured ? Math.Clamp(startStep, 0, 2) : Math.Clamp(startStep, 1, 2);
        if (StepIndex > 0)
        {
            await ScanAsync().ConfigureAwait(true);
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
        WebDavStatus = "正在测试连接…";
        try
        {
            var options = BuildOptions();
            if (!options.IsConfigured)
            {
                WebDavStatus = "地址不是合法 URL（需要 http/https）";
                return;
            }

            var result = await _webDav.TestAsync(options, PasswordInput, CancellationToken.None)
                .ConfigureAwait(true);
            WebDavStatus = result.Message;
        }
        catch (Exception ex)
        {
            WebDavStatus = $"测试失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (StepIndex == 0)
        {
            if (!await SaveWebDavAsync(requireUrl: false).ConfigureAwait(true))
            {
                return;
            }

            StepIndex = 1;
            await ScanAsync().ConfigureAwait(true);
            return;
        }

        if (StepIndex == 1)
        {
            StepIndex = 2;
            FinishSummary = SelectedCount == 0
                ? "还没有勾选游戏：可以直接完成，之后再用「扫描游戏」添加。"
                : $"将添加 {SelectedCount} 个游戏，并为每个游戏立即做一次本地快照（上传在后台进行）。";
        }
    }

    [RelayCommand]
    private async Task SkipWebDavAsync()
    {
        if (IsBusy)
        {
            return;
        }

        StepIndex = 1;
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        ScanStatus = "正在扫描 Steam / Epic / GOG / Ubisoft / Xbox…";
        try
        {
            var result = await _discovery.ScanAsync(new DiscoveryOptions(), CancellationToken.None)
                .ConfigureAwait(true);

            Discovered.Clear();
            foreach (var game in result.Games)
            {
                var item = new DiscoveryItemViewModel(game);
                item.PropertyChanged += OnItemPropertyChanged;
                Discovered.Add(item);
            }

            var withRoots = result.Games.Count(g => g.SuggestedRoots.Count > 0 && !g.AlreadyAdded);
            ScanStatus = result.Games.Count == 0
                ? "没有扫描到已安装的游戏。可以用「手动添加」指定存档目录。"
                : $"共 {result.Games.Count} 个游戏，{withRoots} 个已自动匹配到存档目录" +
                  (result.Warnings.Count > 0 ? $"（{result.Warnings.Count} 条解析警告，见日志）" : string.Empty);

            foreach (var warning in result.Warnings)
            {
                _logger.Warn(warning);
            }

            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(SelectedCount));
        }
        catch (Exception ex)
        {
            _logger.Error("扫描游戏失败", ex);
            ScanStatus = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        AddedCount = 0;
        try
        {
            foreach (var item in Discovered.Where(i => i.IsSelected).ToList())
            {
                var game = _discovery.CreateGame(item.Model, item.Model.SuggestedRoots, _clock.UtcNowMs);
                if (game.SaveRoots.Count == 0)
                {
                    continue;
                }

                await _games.UpsertAsync(game, CancellationToken.None).ConfigureAwait(true);
                AddedCount++;
                _logger.Info($"向导添加游戏：{game.Name}（{game.SaveRoots.Count} 个存档根，来源 {game.Source}）");
            }

            if (AddedCount > 0)
            {
                _logger.Info($"向导完成：新增 {AddedCount} 个游戏");
            }

            Completed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error("向导保存失败", ex);
            FinishSummary = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Discovered.Where(i => i.HasRoots && !i.Model.AlreadyAdded))
        {
            item.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Discovered)
        {
            item.IsSelected = false;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    private async Task<bool> SaveWebDavAsync(bool requireUrl)
    {
        var options = BuildOptions();
        if (!options.IsConfigured)
        {
            if (requireUrl)
            {
                WebDavStatus = "地址不是合法 URL（需要 http/https）";
                return false;
            }

            WebDavStatus = "已跳过：快照只保存在本机";
            return true;
        }

        try
        {
            await _webDav.SaveAsync(options, PasswordInput, CancellationToken.None).ConfigureAwait(true);
            _loaded = options;
            HasStoredPassword = !string.IsNullOrEmpty(options.Password);
            _webDavConfigured = true;
            WebDavStatus = "已保存（密码用 DPAPI 加密存储）";
            return true;
        }
        catch (Exception ex)
        {
            WebDavStatus = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private WebDavOptions BuildOptions() => new()
    {
        BaseUrl = BaseUrl?.Trim() ?? string.Empty,
        RemoteRoot = string.IsNullOrWhiteSpace(RemoteRoot) ? AppPaths.DefaultRemoteRoot : RemoteRoot.Trim(),
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        Password = string.IsNullOrEmpty(PasswordInput) ? _loaded.Password : PasswordInput,
        TrustedCertificateThumbprint = string.IsNullOrWhiteSpace(CertificateThumbprint)
            ? null
            : CertificateThumbprint.Trim(),
        TimeoutSeconds = 60,
    };

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoveryItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
        }
    }
}

/// <summary>探测窗口里的一行候选目录。</summary>
public sealed partial class ProbeCandidateItem : ObservableObject
{
    public ProbeCandidateItem(ProbeCandidate candidate, bool selected)
    {
        Candidate = candidate;
        isSelected = selected;
    }

    public ProbeCandidate Candidate { get; }

    public string Directory => Candidate.Directory;

    public string Reason => Candidate.Reason;

    public string CountText => $"新增 {Candidate.NewFiles} · 修改 {Candidate.ModifiedFiles}";

    public string SizeText => DiagnosticsReport.FormatBytes(Candidate.AddedBytes);

    public string ScoreText => Candidate.Score.ToString("0.#");

    public string SampleText => string.Join("   ", Candidate.Samples.Select(s => Path.GetFileName(s.Path)));

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>探测模式：记录 → 玩 → 对比 → 勾选。</summary>
public sealed partial class ProbeViewModel : ObservableObject
{
    private readonly ProbeService _probe;
    private ProbeStart? _start;
    private readonly ProbeRoots _roots;

    public ProbeViewModel(ProbeService probe, string gameName, string? installDir, string userProfile,
        string appData, string localAppData, string documents, string savedGames)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        GameName = gameName;
        _roots = ProbeService.BuildDefaultRoots(
            userProfile, appData, localAppData, documents, savedGames, installDir);
        RootsText = string.Join(Environment.NewLine, _roots.Roots);
    }

    public event Action? Completed;

    public string GameName { get; }

    public string RootsText { get; }

    public ObservableCollection<ProbeCandidateItem> Candidates { get; } = new();

    [ObservableProperty]
    private string statusText = "点「开始探测」记录当前状态，然后启动游戏玩几分钟，再回来点「完成探测」。";

    [ObservableProperty]
    private string scannedText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool hasStarted;

    [ObservableProperty]
    private bool hasResult;

    public int SelectedCount => Candidates.Count(c => c.IsSelected);

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _start = await _probe.StartAsync(_roots, null, CancellationToken.None).ConfigureAwait(true);
            HasStarted = true;
            ScannedText = $"已记录 {_start.FileCount} 个文件的指纹，用时 {_start.Elapsed.TotalMilliseconds:0} ms";
            StatusText = "现在去启动游戏玩几分钟（最好在游戏里存一次档），退出后再回来点「完成探测」。";
        }
        catch (Exception ex)
        {
            StatusText = $"记录失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (IsBusy || _start is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在对比…";
        try
        {
            var result = await _probe.CompleteAsync(_start, null, CancellationToken.None).ConfigureAwait(true);
            Candidates.Clear();
            var index = 0;
            foreach (var candidate in result.Candidates)
            {
                var item = new ProbeCandidateItem(candidate, selected: index < 5);
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ProbeCandidateItem.IsSelected))
                    {
                        OnPropertyChanged(nameof(SelectedCount));
                    }
                };
                Candidates.Add(item);
                index++;
            }

            HasResult = true;
            ScannedText = $"扫描 {result.ScannedFiles} 个文件，{result.ChangedFiles} 个发生变化，用时 {result.Elapsed.TotalMilliseconds:0} ms";
            StatusText = Candidates.Count == 0
                ? "没有发现可疑目录。确认游戏确实写了存档，或改用「手动添加」直接指定目录。"
                : $"找到 {Candidates.Count} 个候选目录，按可能性排序；勾选正确的那个再点「使用所选目录」。";
            OnPropertyChanged(nameof(SelectedCount));
        }
        catch (Exception ex)
        {
            StatusText = $"对比失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void UseSelected()
    {
        if (Candidates.All(c => !c.IsSelected))
        {
            StatusText = "至少勾选一个目录";
            return;
        }

        Completed?.Invoke();
    }

    public IReadOnlyList<string> SelectedDirectories =>
        Candidates.Where(c => c.IsSelected).Select(c => c.Directory).ToList();
}
