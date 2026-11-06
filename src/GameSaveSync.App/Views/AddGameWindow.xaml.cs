using System.Windows;
using System.Windows.Controls;
using GameSaveSync.Core.Online;
using Microsoft.Win32;

namespace GameSaveSync.App.Views;

public partial class AddGameWindow : Window
{
    private readonly IOnlineSavePathProvider _onlinePaths;

    public AddGameWindow(IOnlineSavePathProvider onlinePaths)
    {
        InitializeComponent();
        _onlinePaths = onlinePaths ?? throw new ArgumentNullException(nameof(onlinePaths));
        Loaded += (_, _) => NameBox.Focus();
    }

    public string GameName => NameBox.Text.Trim();

    public IReadOnlyList<string> Directories =>
        DirectoryList.Items.Cast<string>().ToList();

    public string Excludes => ExcludeBox.Text;

    public string? InstallDir => string.IsNullOrWhiteSpace(InstallDirBox.Text) ? null : InstallDirBox.Text.Trim();

    public IReadOnlyList<string> ExeNames => ExeNamesBox.Text
        .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>按名字去 PCGamingWiki 查已知存档路径；结果以令牌形式（%USERPROFILE%\…）加入目录列表。</summary>
    private async void QueryOnline_OnClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            QueryStatus.Text = "先填游戏名称，再点联网查询。";
            return;
        }

        QueryButton.IsEnabled = false;
        var original = QueryButton.Content;
        QueryButton.Content = "查询中…";
        QueryStatus.Text = "正在查询 PCGamingWiki…";
        try
        {
            var results = await _onlinePaths.QueryAsync(name, CancellationToken.None);

            var added = 0;
            foreach (var result in results)
            {
                // 依赖安装目录的模板（{installDir}）在用户没填安装目录时无法定位，先跳过
                if (result.PathTemplate.Contains("{installDir}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = result.PathTemplate.Trim().TrimEnd('\\', '/');
                if (DirectoryList.Items.Contains(path))
                {
                    continue;
                }

                DirectoryList.Items.Add(path);
                added++;
            }

            QueryStatus.Text = results.Count == 0
                ? "网上没查到该游戏的存档路径，试试英文名，或手动指定目录。"
                : added > 0
                    ? $"查到 {results.Count} 条，已加入 {added} 个目录（来源 {results[0].SourceUrl}）。"
                    : $"查到 {results.Count} 条，但都依赖安装目录；先填「安装目录」再点一次。";
        }
        catch (Exception ex)
        {
            QueryStatus.Text = $"查询失败（网络问题）：{ex.Message}";
        }
        finally
        {
            QueryButton.IsEnabled = true;
            QueryButton.Content = original;
        }
    }

    private void BrowseInstallDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择游戏安装目录", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            InstallDirBox.Text = dialog.FolderName;
        }
    }

    private void AddDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择存档目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!DirectoryList.Items.Contains(dialog.FolderName))
        {
            DirectoryList.Items.Add(dialog.FolderName);
        }
    }

    private void RemoveDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        if (DirectoryList.SelectedItem is string selected)
        {
            DirectoryList.Items.Remove(selected);
        }
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "请填写游戏名称。", "Reload", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (DirectoryList.Items.Count == 0)
        {
            MessageBox.Show(this, "请至少添加一个存档目录。", "Reload", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
