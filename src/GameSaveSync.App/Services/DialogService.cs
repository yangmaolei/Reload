using System.Windows;
using GameSaveSync.App.ViewModels;
using GameSaveSync.App.Views;
using GameSaveSync.Core.Locator;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Probe;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Restore;
using GameSaveSync.Core.Retention;
using Microsoft.Extensions.DependencyInjection;

namespace GameSaveSync.App.Services;

/// <summary>WPF 弹窗实现。ViewModel 只依赖 <see cref="IDialogService"/>，便于替换/测试。</summary>
public sealed class DialogService : IDialogService
{
    private readonly IServiceProvider _services;

    public DialogService(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    private static Window? Owner => Application.Current?.MainWindow;

    public AddGameResult? ShowAddGame()
    {
        var window = new AddGameWindow(_services.GetRequiredService<GameSaveSync.Core.Online.IOnlineSavePathProvider>())
        {
            Owner = Owner,
        };
        if (window.ShowDialog() != true)
        {
            return null;
        }

        return new AddGameResult(
            window.GameName,
            window.Directories,
            window.Excludes,
            window.InstallDir,
            window.ExeNames);
    }

    public bool ConfirmRetention(RetentionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var preview = string.Join(Environment.NewLine, plan.Snapshots
            .OrderBy(s => s.CreatedAt)
            .Take(8)
            .Select(s => $"  · {GameSaveSync.Core.Diagnostics.TimeUtil.FormatLocal(s.CreatedAt)}  " +
                         $"{s.Trigger.ToDisplayName()}  {s.FileCount} 文件"));

        var message = $"{plan.Describe()}{Environment.NewLine}{Environment.NewLine}" +
                      $"将清理以下快照（手动快照与 7 天内的 pre_restore 不会被删）：{Environment.NewLine}" +
                      preview +
                      (plan.Snapshots.Count > 8 ? $"{Environment.NewLine}  …（共 {plan.Snapshots.Count} 个）" : string.Empty);

        return MessageWindow.Confirm(Owner, "清理旧快照", message, "清理", "取消", MessageWindow.Kind.Warning);
    }

    public ConflictChoice ShowConflict(ConflictView view, out Guid leftId, out Guid rightId)
    {
        ArgumentNullException.ThrowIfNull(view);
        var window = new ConflictWindow(view) { Owner = Owner };
        leftId = window.LeftSnapshotId;
        rightId = window.RightSnapshotId;

        return window.ShowDialog() == true ? window.Choice : ConflictChoice.None;
    }

    public IReadOnlyDictionary<string, string>? ShowRelocate(string gameName, IReadOnlyList<string> unresolvedRoots)
    {
        var window = new RelocateWindow(gameName, unresolvedRoots) { Owner = Owner };
        return window.ShowDialog() == true ? window.Mapping : null;
    }

    public string? PickSaveFile(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出快照",
            FileName = suggestedName,
            DefaultExt = ".zip",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmRestore(RestorePlan plan)
    {
        var window = new RestoreConfirmWindow(plan) { Owner = Owner };
        return window.ShowDialog() == true;
    }

    public string? PromptNote(string title, string initial = "")
    {
        var window = new NoteWindow(title, initial) { Owner = Owner };
        return window.ShowDialog() == true ? window.Note : null;
    }

    public int ShowWizard(int startStep)
    {
        var viewModel = _services.GetRequiredService<WizardViewModel>();
        var window = new WizardWindow(viewModel, startStep) { Owner = Owner };
        window.ShowDialog();
        return viewModel.AddedCount;
    }

    public IReadOnlyList<string>? ShowProbe(string gameName, string? installDir)
    {
        var probe = _services.GetRequiredService<ProbeService>();
        var environment = _services.GetRequiredService<ILocatorEnvironment>();

        var viewModel = new ProbeViewModel(
            probe,
            gameName,
            installDir,
            environment.UserProfile,
            environment.AppData,
            environment.LocalAppData,
            environment.Documents,
            environment.SavedGames);

        var window = new ProbeWindow(viewModel) { Owner = Owner };
        return window.ShowDialog() == true ? viewModel.SelectedDirectories : null;
    }

    public void ShowInfo(string message, string title = "Reload") =>
        MessageWindow.Info(Owner, title, message);

    public void ShowError(string message, string title = "出错了") =>
        MessageWindow.Error(Owner, title, message);
}
