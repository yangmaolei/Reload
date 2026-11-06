using System.Windows;
using GameSaveSync.Core.Diagnostics;
using GameSaveSync.Core.Remote;
using GameSaveSync.Core.Restore;

namespace GameSaveSync.App.Views;

public enum ConflictChoice
{
    None = 0,
    Left = 1,
    Right = 2,
    ExportBoth = 3,
}

public partial class ConflictWindow : Window
{
    private readonly ConflictView _view;

    public ConflictWindow(ConflictView view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        InitializeComponent();

        HeaderText.Text = $"「{view.GameName}」存在同步冲突";
        LeftTime.Text = TimeUtil.FormatLocal(view.Left.Snapshot.CreatedAt);
        LeftDevice.Text = $"来源设备：{view.Left.DeviceName} · {view.Left.FileCount} 个文件 · " +
                          $"{RestorePlan.FormatBytes(view.Left.TotalSize)}";
        LeftDiff.Text = $"相对共同祖先：+{view.Left.AddedFiles} 新增 / ~{view.Left.ModifiedFiles} 修改 / " +
                        $"-{view.Left.DeletedFiles} 删除";

        RightTime.Text = TimeUtil.FormatLocal(view.Right.Snapshot.CreatedAt);
        RightDevice.Text = $"来源设备：{view.Right.DeviceName} · {view.Right.FileCount} 个文件 · " +
                           $"{RestorePlan.FormatBytes(view.Right.TotalSize)}";
        RightDiff.Text = $"相对共同祖先：+{view.Right.AddedFiles} 新增 / ~{view.Right.ModifiedFiles} 修改 / " +
                         $"-{view.Right.DeletedFiles} 删除";
    }

    public ConflictChoice Choice { get; private set; } = ConflictChoice.None;

    public Guid LeftSnapshotId => _view.Left.Snapshot.Id;

    public Guid RightSnapshotId => _view.Right.Snapshot.Id;

    private void ChooseLeft_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.Left;
        DialogResult = true;
    }

    private void ChooseRight_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.Right;
        DialogResult = true;
    }

    private void ExportBoth_OnClick(object sender, RoutedEventArgs e)
    {
        Choice = ConflictChoice.ExportBoth;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
