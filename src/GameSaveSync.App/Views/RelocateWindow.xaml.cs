using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace GameSaveSync.App.Views;

public sealed class RelocateItem : INotifyPropertyChanged
{
    private string _localPath = string.Empty;

    public RelocateItem(string token) => Token = token;

    public string Token { get; }

    public string LocalPath
    {
        get => _localPath;
        set
        {
            if (_localPath == value)
            {
                return;
            }

            _localPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalPath)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class RelocateWindow : Window
{
    public RelocateWindow(string gameName, IEnumerable<string> unresolvedRoots)
    {
        InitializeComponent();
        HeaderText.Text = $"「{gameName}」需要重新定位";

        Items = new ObservableCollection<RelocateItem>(unresolvedRoots.Select(r => new RelocateItem(r)));
        RootList.ItemsSource = Items;
    }

    public ObservableCollection<RelocateItem> Items { get; }

    public IReadOnlyDictionary<string, string> Mapping =>
        Items.Where(i => !string.IsNullOrWhiteSpace(i.LocalPath))
            .ToDictionary(i => i.Token, i => i.LocalPath.Trim(), StringComparer.OrdinalIgnoreCase);

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RelocateItem item })
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = $"为 {item.Token} 选择本机目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            item.LocalPath = dialog.FolderName;
        }
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (Mapping.Count == 0)
        {
            MessageBox.Show(this, "请至少为一个存档根指定目录。", "Reload", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
