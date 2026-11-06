using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameSaveSync.App.Services;
using GameSaveSync.App.ViewModels;

namespace GameSaveSync.App.Views;

public partial class WizardWindow : Window
{
    private readonly WizardViewModel _viewModel;
    private readonly int _startStep;

    public WizardWindow(WizardViewModel viewModel, int startStep)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _startStep = startStep;
        InitializeComponent();
        DataContext = _viewModel;
        WindowSizing.FitToWorkArea(this);
        _viewModel.Completed += OnCompleted;
        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Completed -= OnCompleted;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync(_startStep);
        }
        catch (Exception ex)
        {
            _viewModel.WebDavStatus = $"初始化失败：{ex.Message}";
        }
    }

    private void OnCompleted()
    {
        DialogResult = true;
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            _viewModel.PasswordInput = box.Password;
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
