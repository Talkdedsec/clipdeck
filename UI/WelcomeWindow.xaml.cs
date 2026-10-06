using System.ComponentModel;
using System.Windows;

namespace ClipDeck.UI;

public partial class WelcomeWindow : Window
{
    static App AppRef => (App)Application.Current;
    bool _applied;

    public WelcomeWindow()
    {
        InitializeComponent();
        var s = AppRef.Settings;
        OptWinV.IsChecked = s.TakeOverWinV;
        OptWinPeriod.IsChecked = s.TakeOverWinPeriod;
        OptAutostart.IsChecked = true;
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Loaded += (_, _) => Activate();
    }

    void Start_Click(object sender, RoutedEventArgs e) => Close();

    // Closing with the X keeps the choices shown, so the window never comes back on the next start.
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_applied) return;
        _applied = true;
        var s = AppRef.Settings;
        s.TakeOverWinV = OptWinV.IsChecked == true;
        s.TakeOverWinPeriod = OptWinPeriod.IsChecked == true;
        s.StartWithWindows = OptAutostart.IsChecked == true;
        s.WelcomeShown = true;
        AppRef.ApplySettings();
    }
}
