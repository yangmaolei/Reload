using System.Windows;
using System.Windows.Controls;
using GameSaveSync.App.ViewModels;

namespace GameSaveSync.App.Views.Pages;

public partial class SettingsPage : UserControl
{
    private bool _syncingRadios;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_OnLoaded;
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is SettingsViewModel settings)
        {
            settings.PasswordInput = box.Password;
        }
    }

    /// <summary>主题色圆点点击：按 index 调 VM 命令。</summary>
    private void SeedSwatch_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ThemeSwatch swatch } &&
            DataContext is SettingsViewModel settings)
        {
            var index = settings.ThemeSwatches.IndexOf(swatch);
            if (index >= 0)
            {
                settings.SelectSeedCommand.Execute(index);
            }
        }
    }

    /// <summary>外观模式单选：按 Tag（0/1/2）调 VM 命令；初始同步期间不触发。</summary>
    private void ThemeMode_OnChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingRadios || DataContext is not SettingsViewModel settings)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tag } &&
            int.TryParse(tag, out var index))
        {
            settings.SelectModeCommand.Execute(index);
        }
    }

    /// <summary>页面加载后把当前主题状态同步到单选钮（不触发命令）。</summary>
    private void SettingsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        _syncingRadios = true;
        try
        {
            ModeSystem.IsChecked = settings.SelectedModeIndex == 0;
            ModeLight.IsChecked = settings.SelectedModeIndex == 1;
            ModeDark.IsChecked = settings.SelectedModeIndex == 2;
        }
        finally
        {
            _syncingRadios = false;
        }
    }
}
