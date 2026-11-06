using System.Windows;
using GameSaveSync.Core.Restore;

namespace GameSaveSync.App.Views;

public partial class RestoreConfirmWindow : Window
{
    public RestoreConfirmWindow(RestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitializeComponent();

        HeaderText.Text = $"恢复到「{plan.GameName}」的 {TimeUtilText(plan.SnapshotCreatedAt)} 快照";
        SummaryText.Text = plan.Describe();
        OperationGrid.ItemsSource = plan.Operations
            .OrderBy(o => o.Kind)
            .Take(500)
            .Select(o => new
            {
                KindText = o.Kind switch
                {
                    RestoreOperationKind.Add => "新增",
                    RestoreOperationKind.Overwrite => "覆盖",
                    _ => "删除",
                },
                RelativePath = o.RelPath,
                SizeText = RestorePlan.FormatBytes(o.Size),
            })
            .ToList();
    }

    private static string TimeUtilText(long unixMs) =>
        GameSaveSync.Core.Diagnostics.TimeUtil.FormatLocal(unixMs);

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
