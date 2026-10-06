using System.Diagnostics;
using System.Windows;
using ClipDeck.Core;
using Forms = System.Windows.Forms;

namespace ClipDeck.UI;

internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _pause;
    readonly Forms.ToolStripMenuItem _update;
    readonly Forms.ToolStripSeparator _updateLine;
    string? _updateUrl;
    string? _balloonUrl;

    public TrayIcon(App app)
    {
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true };

        var menu = new Forms.ContextMenuStrip();
        _update = new Forms.ToolStripMenuItem("", null, (_, _) => OpenUrl(_updateUrl)) { Visible = false };
        _updateLine = new Forms.ToolStripSeparator { Visible = false };
        menu.Items.Add(_update);
        menu.Items.Add(_updateLine);
        menu.Items.Add(Loc.T("Aç  (Win+V)"), null, (_, _) => app.ShowPopup(IntPtr.Zero));
        _pause = new Forms.ToolStripMenuItem(Loc.T("Kaydı duraklat"), null, (_, _) => app.TogglePause());
        menu.Items.Add(_pause);
        menu.Items.Add(Loc.T("Ayarlar"), null, (_, _) => app.OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Çıkış"), null, (_, _) => app.Quit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ShowPopup(IntPtr.Zero);
        };
        _icon.BalloonTipClicked += (_, _) => OpenUrl(_balloonUrl);
        SetPaused(false);
    }

    static System.Drawing.Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/clipdeck.ico"));
        using var stream = res.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void SetPaused(bool paused)
    {
        _pause.Checked = paused;
        _icon.Text = (paused ? Loc.T("clipdeck — kayıt duraklatıldı") : "clipdeck — Win+V")
            + (Autostart.IsElevated ? " (" + Loc.T("yönetici") + ")" : "");
    }

    public void SetUpdate(UpdateInfo info)
    {
        _updateUrl = info.Url;
        _update.Text = Loc.F("Yeni sürüm: {0}", info.Version.ToString(3));
        _update.Visible = _updateLine.Visible = true;
    }

    // A balloon with a link opens it when clicked; a later plain balloon clears the link.
    public void Balloon(string title, string text, string? url = null)
    {
        _balloonUrl = url;
        _icon.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.None);
    }

    static void OpenUrl(string? url)
    {
        if (url is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("bağlantı açılamadı", ex);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
