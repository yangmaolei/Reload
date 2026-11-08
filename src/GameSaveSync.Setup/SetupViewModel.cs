using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameSaveSync.Setup;

/// <summary>向导步骤。</summary>
public enum SetupStep
{
    Welcome = 0,
    Scope = 1,
    Location = 2,
    Options = 3,
    Installing = 4,
    Finished = 5,
}

/// <summary>
/// 步骤的显示文本。放在这里而不是 XAML 里，是为了让左侧导航条
/// 和内容区共用同一份文案，不会出现两处描述不一致。
/// </summary>
public static class SetupStepText
{
    public static string Title(SetupStep step)
    {
        switch (step)
        {
            case SetupStep.Welcome: return "欢迎";
            case SetupStep.Scope: return "安装范围";
            case SetupStep.Location: return "安装位置";
            case SetupStep.Options: return "安装选项";
            case SetupStep.Installing: return "正在安装";
            case SetupStep.Finished: return "完成";
            default: return step.ToString();
        }
    }

    public static string Hint(SetupStep step)
    {
        switch (step)
        {
            case SetupStep.Welcome: return "开始";
            case SetupStep.Scope: return "仅当前用户或所有用户";
            case SetupStep.Location: return "选择程序目录";
            case SetupStep.Options: return "快捷方式与自启动";
            case SetupStep.Installing: return "复制文件";
            case SetupStep.Finished: return "完成";
            default: return string.Empty;
        }
    }
}

/// <summary>
/// 安装向导的状态机。UI 只做渲染，所有流转逻辑都在这里，方便单测。
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly Installer _installer;

    /// <summary>供 XAML 用的无参构造（不用于实际安装流程）。</summary>
    public SetupViewModel()
        : this(CreateDefaultInstaller())
    {
    }

    [ObservableProperty]
    private SetupStep _current = SetupStep.Welcome;

    [ObservableProperty]
    private InstallScope _scope = InstallScope.CurrentUser;

    [ObservableProperty]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private bool _createShortcut = true;

    [ObservableProperty]
    private bool _enableAutoStart;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private bool _finishedSuccessfully;

    [ObservableProperty]
    private string _finishedMessage = string.Empty;

    /// <summary>已完成的步骤名（安装进度用）。</summary>
    public ObservableCollection<string> CompletedSteps { get; } = new();

    /// <summary>
    /// 默认安装器：优先用嵌入在安装器里的 payload，
    /// 没有嵌入资源时退回「同目录 payload\Reload.exe」（开发调试）。
    /// </summary>
    private static Installer CreateDefaultInstaller()
    {
        const string resourceName = "GameSaveSync.Setup.payload.Reload.exe";

        var names = typeof(SetupViewModel).Assembly.GetManifestResourceNames();
        var embedded = names.FirstOrDefault(n => n.EndsWith("payload.Reload.exe", StringComparison.OrdinalIgnoreCase));

        return new Installer("payload\\Reload.exe", embedded ?? (names.Contains(resourceName) ? resourceName : null));
    }

    public SetupViewModel(Installer installer)
    {
        _installer = installer;
        _installDirectory = SetupPaths.DefaultCurrentUserDirectory();

        foreach (var step in AllSteps)
        {
            StepList.Add(new StepIndicatorItem { Step = step });
        }

        UpdateStepIndicators();
    }

    /// <summary>总步骤数（用于左侧导航的指示点）。</summary>
    public IReadOnlyList<SetupStep> AllSteps { get; } = new[]
    {
        SetupStep.Welcome,
        SetupStep.Scope,
        SetupStep.Location,
        SetupStep.Options,
        SetupStep.Installing,
        SetupStep.Finished,
    };

    /// <summary>左侧导航条用的步骤项集合。</summary>
    public ObservableCollection<StepIndicatorItem> StepList { get; } = new();

    /// <summary>
    /// 左侧导航条的一项。绑定用，属性变化会通知 UI。
    /// </summary>
    public sealed class StepIndicatorItem : ObservableObject
    {
        private bool _isCurrent;
        private bool _isReached;

        public SetupStep Step { get; init; }

        public string Title => SetupStepText.Title(Step);

        public string Hint => SetupStepText.Hint(Step);

        /// <summary>是否是当前所在步骤。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }

        /// <summary>是否已经走过（当前步之前 + 当前步）。</summary>
        public bool IsReached
        {
            get => _isReached;
            set => SetProperty(ref _isReached, value);
        }
    }

    /// <summary>当前步骤在导航中的序号（1 起）。</summary>
    public int CurrentStepIndex => (int)Current + 1;

    /// <summary>是否是最后一步（完成页）。</summary>
    public bool IsFinished => Current == SetupStep.Finished;

    /// <summary>是否可以后退。</summary>
    public bool CanGoBack => Current is SetupStep.Scope or SetupStep.Location or SetupStep.Options;

    /// <summary>是否可以前进（完成页不能前进）。</summary>
    public bool CanGoNext => Current is not (SetupStep.Installing or SetupStep.Finished);

    /// <summary>下一步按钮的文字。</summary>
    public string NextButtonText => Current == SetupStep.Options ? "安装" : "下一步";

    /// <summary>安装目录是否合法。</summary>
    public bool IsDirectoryValid =>
        SetupPaths.IsDirectoryAcceptable(InstallDirectory, out _);

    /// <summary>安装目录的错误提示（合法时为空）。</summary>
    public string DirectoryError =>
        SetupPaths.IsDirectoryAcceptable(InstallDirectory, out var reason) ? string.Empty : reason;

    /// <summary>「所有用户」是否需要提权提示。</summary>
    public bool RequiresElevationNotice => Scope == InstallScope.AllUsers;

    partial void OnScopeChanged(InstallScope value)
    {
        // 切换范围时把目录切到对应默认值
        InstallDirectory = SetupPaths.DefaultDirectory(value);
        OnPropertyChanged(nameof(RequiresElevationNotice));
    }

    partial void OnInstallDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirectoryValid));
        OnPropertyChanged(nameof(DirectoryError));
    }

    partial void OnCurrentChanged(SetupStep value)
    {
        OnPropertyChanged(nameof(CurrentStepIndex));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextButtonText));

        UpdateStepIndicators();
    }

    /// <summary>刷新左侧导航条的当前步 / 已到达标记。</summary>
    private void UpdateStepIndicators()
    {
        foreach (var item in StepList)
        {
            item.IsCurrent = item.Step == Current;
            item.IsReached = (int)item.Step <= (int)Current;
        }
    }

    /// <summary>构造当前选择的安装计划。</summary>
    public InstallPlan BuildPlan()
    {
        return new InstallPlan
        {
            Scope = Scope,
            InstallDirectory = InstallDirectory,
            CreateShortcut = CreateShortcut,
            EnableAutoStart = EnableAutoStart,
        };
    }

    /// <summary>后退一步。</summary>
    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        Current--;
    }

    /// <summary>
    /// 前进一步；到最后一步「选项」时改为真正执行安装。
    /// </summary>
    public async Task<bool> GoNextAsync()
    {
        if (!CanGoNext)
        {
            return false;
        }

        if (Current != SetupStep.Options)
        {
            Current++;
            return true;
        }

        await InstallAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 执行安装。任一步失败即停在失败状态并给出原因。
    /// </summary>
    public async Task InstallAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var plan = BuildPlan();

        if (!SetupPaths.IsDirectoryAcceptable(plan.InstallDirectory, out var reason))
        {
            FinishedSuccessfully = false;
            FinishedMessage = reason;
            Current = SetupStep.Finished;
            return;
        }

        IsBusy = true;
        Current = SetupStep.Installing;
        CompletedSteps.Clear();
        ProgressPercent = 0;
        ProgressText = "准备中…";
        StatusText = "正在安装 Reload，请稍候";

        try
        {
            var steps = _installer.BuildSteps(plan);
            var progress = new Progress<InstallStep>(step =>
            {
                ProgressText = step.Description;
            });

            var outcome = await _installer
                .RunAsync(plan, steps, progress, CancellationToken.None)
                .ConfigureAwait(true);

            foreach (var done in outcome.CompletedSteps)
            {
                CompletedSteps.Add(done);
            }

            ProgressPercent = 100;

            FinishedSuccessfully = outcome.Success;
            FinishedMessage = outcome.Success
                ? $"Reload 已安装到 {plan.InstallDirectory}"
                : outcome.Message;

            if (outcome.Success)
            {
                StatusText = "安装完成";
            }
        }
        catch (Exception ex)
        {
            FinishedSuccessfully = false;
            FinishedMessage = $"安装失败：{ex.Message}";
            StatusText = "安装失败";
        }
        finally
        {
            IsBusy = false;
            Current = SetupStep.Finished;

            // 释放出来的临时 payload 用完就删
            _installer.Cleanup();
        }
    }
}
