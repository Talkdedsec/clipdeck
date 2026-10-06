using System.Windows;
using ClipDeck.Core;

namespace ClipDeck.UI;

public partial class SetupWindow : Window
{
    enum Stage { Install, Uninstall, Done }

    Stage _stage;

    public SetupWindow(bool uninstall)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        InstallPath.Text = Loc.F("Kurulacak yer: {0}", Installer.InstallDir);
        SetStage(uninstall ? Stage.Uninstall : Stage.Install);
    }

    void SetStage(Stage stage)
    {
        _stage = stage;
        InstallPanel.Visibility = stage == Stage.Install ? Visibility.Visible : Visibility.Collapsed;
        UninstallPanel.Visibility = stage == Stage.Uninstall ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = stage == Stage.Done ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = stage == Stage.Done ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.Visibility = stage == Stage.Install && Installer.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        (Title, Heading.Text, PrimaryButton.Content) = stage switch
        {
            Stage.Install when Installer.IsInstalled => (Loc.T("clipdeck kurulumu"), Loc.T("clipdeck'i güncelle"), Loc.T("Güncelle")),
            Stage.Install => (Loc.T("clipdeck kurulumu"), Loc.T("clipdeck'i kur"), Loc.T("Kur")),
            Stage.Uninstall => (Loc.T("clipdeck'i kaldır"), Loc.T("clipdeck'i kaldır"), Loc.T("Kaldır")),
            _ => (Title, Heading.Text, Loc.T("Kapat")),
        };
    }

    async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Done:
                Close();
                return;
            case Stage.Install:
                bool startMenu = OptStartMenu.IsChecked == true, desktop = OptDesktop.IsChecked == true;
                bool autostart = OptAutostart.IsChecked == true, launch = OptLaunch.IsChecked == true;
                bool update = Installer.IsInstalled;
                if (await Run(Loc.T("Kuruluyor…"), () => Installer.Install(startMenu, desktop, autostart, launch)))
                    Done(update ? Loc.T("clipdeck güncellendi.") : Loc.T("Kurulum tamamlandı."),
                        launch ? Loc.T("clipdeck çalışıyor; Win+V ile açabilirsin.") : Loc.T("Başlat menüsünden açabilirsin."));
                return;
            case Stage.Uninstall:
                bool removeData = OptRemoveData.IsChecked == true;
                if (await Run(Loc.T("Kaldırılıyor…"), () => Installer.Uninstall(removeData)))
                    Done(Loc.T("clipdeck kaldırıldı."), removeData ? Loc.T("Geçmiş ve ayarlar da silindi.") : Loc.F("Geçmiş ve ayarlar şurada duruyor: {0}", App.DataDir));
                return;
        }
    }

    void Uninstall_Click(object sender, RoutedEventArgs e) => SetStage(Stage.Uninstall);

    async Task<bool> Run(string busyText, Action work)
    {
        PrimaryButton.IsEnabled = CancelButton.IsEnabled = UninstallButton.IsEnabled = false;
        PrimaryButton.Content = busyText;
        try
        {
            await Task.Run(work);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("kurulum", ex);
            MessageBox.Show(this, Loc.T("İşlem tamamlanamadı: ") + ex.Message, "clipdeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStage(_stage);
            return false;
        }
        finally
        {
            PrimaryButton.IsEnabled = CancelButton.IsEnabled = UninstallButton.IsEnabled = true;
        }
    }

    void Done(string heading, string text)
    {
        SetStage(Stage.Done);
        Heading.Text = heading;
        DoneText.Text = text;
    }
}
