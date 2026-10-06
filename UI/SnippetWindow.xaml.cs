using System.Windows;
using System.Windows.Input;
using ClipDeck.Core;

namespace ClipDeck.UI;

public partial class SnippetWindow : Window
{
    public string SnippetTitle => TitleBox.Text.Trim();
    public string SnippetText => ContentBox.Text;

    public SnippetWindow(string caption, string title, string text)
    {
        InitializeComponent();
        Title = caption;
        VarsHint.Text = Loc.T("Değişkenler (yapıştırırken doldurulur): ") + SnippetVars.Help;
        TitleBox.Text = title;
        ContentBox.Text = text;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            Activate();
            var box = string.IsNullOrEmpty(title) ? TitleBox : ContentBox;
            box.Focus();
            box.CaretIndex = box.Text.Length;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Save_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            Error.Text = Loc.T("İçerik boş olamaz.");
            ContentBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            var first = ContentBox.Text.Trim().Split('\n')[0].Trim();
            TitleBox.Text = first.Length > 40 ? first[..40].TrimEnd() + "…" : first;
        }
        DialogResult = true;
    }
}
