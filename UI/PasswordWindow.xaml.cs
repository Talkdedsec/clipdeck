using System.Windows;
using ClipDeck.Core;

namespace ClipDeck.UI;

public partial class PasswordWindow : Window
{
    readonly bool _confirm;

    public string Password => Pass1.Password;

    public PasswordWindow(string heading, string info, bool confirm)
    {
        InitializeComponent();
        _confirm = confirm;
        Heading.Text = heading;
        Info.Text = info;
        ConfirmLabel.Visibility = Pass2.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Loaded += (_, _) => Pass1.Focus();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_confirm && Pass1.Password.Length < 8)
        {
            Error.Text = Loc.T("Parola en az 8 karakter olmalı.");
            return;
        }
        if (_confirm && Pass1.Password != Pass2.Password)
        {
            Error.Text = Loc.T("Parolalar aynı değil.");
            return;
        }
        if (Pass1.Password.Length == 0)
        {
            Error.Text = Loc.T("Parola gir.");
            return;
        }
        DialogResult = true;
    }
}
