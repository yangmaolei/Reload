using System.Windows;

namespace GameSaveSync.App.Views;

public partial class NoteWindow : Window
{
    public NoteWindow(string title, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = title;
        NoteBox.Text = initial;
        Loaded += (_, _) =>
        {
            NoteBox.Focus();
            NoteBox.SelectAll();
        };
    }

    public string Note => NoteBox.Text;

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
