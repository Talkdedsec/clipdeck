using System.Windows;
using ClipDeck.Core;
using Forms = System.Windows.Forms;

namespace ClipDeck.UI;

internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ToolStripMenuItem _pause;

    public TrayIcon(App app)
    {
        _icon = new Forms.NotifyIcon { Icon = LoadIcon(), Visible = true };

        var menu = new Forms.ContextMenuStrip();
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

    public void Balloon(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
