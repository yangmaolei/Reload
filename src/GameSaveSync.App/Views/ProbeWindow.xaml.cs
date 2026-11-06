using System.Windows;
using System.Windows.Input;
using GameSaveSync.App.Services;
using GameSaveSync.App.ViewModels;

namespace GameSaveSync.App.Views;

public partial class ProbeWindow : Window
{
    private readonly ProbeViewModel _viewModel;

    public ProbeWindow(ProbeViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;
        WindowSizing.FitToWorkArea(this);
        _viewModel.Completed += OnCompleted;
        Closed += (_, _) => _viewModel.Completed -= OnCompleted;
    }

    private void OnCompleted() => DialogResult = true;

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        // 校验没通过时 UseSelected 会写状态文字，这里不关窗
        if (_viewModel.SelectedDirectories.Count > 0)
        {
            DialogResult = true;
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>标题栏拖拽：把弹窗拖到别处（交互控件上的点击不受影响）。</summary>
    private void Title_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        WindowDrag.TryDrag(this, e);

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
