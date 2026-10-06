using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClipDeck.Core;
using ClipDeck.Native;

namespace ClipDeck.UI;

// Shown beside the panel without taking focus, so typing and arrow keys keep working in the panel.
public partial class PreviewWindow : Window
{
    const int MaxChars = 100_000;
    const int PreviewWidth = 460;
    const long WS_EX_NOACTIVATE = 0x08000000;

    IntPtr _hwnd;

    public PreviewWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex = (ex | Win32.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
            Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        };
    }

    internal void ShowFor(ClipItem item, ClipPayload payload, IntPtr beside)
    {
        Fill(item, payload);
        Scroll.ScrollToTop();
        if (!IsVisible) Show();
        PlaceBeside(beside);
    }

    public void HidePreview(bool release)
    {
        Hide();
        if (!release) return;
        ImageView.Source = null;
        TextView.Text = CodeView.Text = ExtraView.Text = "";
        CodeText.SetSource(CodeView, null);
    }

    public void ScrollBy(double delta) => Scroll.ScrollToVerticalOffset(Scroll.VerticalOffset + delta);

    void PlaceBeside(IntPtr popup)
    {
        if (!Win32.GetWindowRect(popup, out var p)) return;
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(Win32.MonitorFromPoint(new Win32.POINT { X = p.Left, Y = p.Top }, Win32.MONITOR_DEFAULTTONEAREST), ref mi);
        var wa = mi.rcWork;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(PreviewWidth * scale), h = p.Bottom - p.Top, gap = (int)Math.Round(8 * scale);
        int x = p.Right + gap + w <= wa.Right ? p.Right + gap : Math.Max(wa.Left, p.Left - gap - w);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, p.Top, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
    }

    void Fill(ClipItem item, ClipPayload p)
    {
        foreach (var e in new UIElement[] { Note, TextView, CodeView, ColorView, ImageView, ExtraView })
            e.Visibility = Visibility.Collapsed;
        ImageView.Source = null;
        Glyph.Text = item.KindGlyph;
        Footer.Text = string.Join(" · ", new[]
        {
            item.AppName,
            DateTimeOffset.FromUnixTimeMilliseconds(item.Created).LocalDateTime.ToString("d MMMM yyyy HH:mm", Loc.Culture),
        }.Where(s => !string.IsNullOrEmpty(s)));

        if (item.IsSensitive && ClipItem.MaskSensitive)
        {
            Heading.Text = Loc.T("Gizli öğe");
            ShowNote(Loc.T("Bu öğe hassas veri içeriyor; önizlemede maskeli gösterilir. Yapıştırınca tam hali gider."));
            ShowText(TextView, item.Preview);
            return;
        }

        switch (item.Kind)
        {
            case ClipKind.Image:
                Heading.Text = Loc.F("Resim · {0}×{1}", item.Width, item.Height);
                if (p.Image is not null)
                {
                    int maxPx = (int)Math.Ceiling(PreviewWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
                    ImageView.Source = item.Width > maxPx ? ImageUtil.DecodeToWidth(p.Image, maxPx) : ImageUtil.Decode(p.Image);
                    ImageView.MaxHeight = Math.Max(120, item.Height);
                    ImageView.Visibility = Visibility.Visible;
                }
                if (!string.IsNullOrWhiteSpace(item.OcrText))
                    ShowText(ExtraView, Loc.T("Resimdeki yazı:") + "\n" + item.OcrText);
                break;

            case ClipKind.Files:
                var files = p.Files ?? [];
                Heading.Text = Loc.F("{0} dosya", files.Length);
                ShowText(TextView, string.Join("\n\n", files.Select(Describe)));
                break;

            default:
                ShowTextItem(item, p.Text ?? "");
                break;
        }
    }

    void ShowTextItem(ClipItem item, string text)
    {
        int lines = text.Count(c => c == '\n') + 1;
        if (item.IsSnippet)
        {
            Heading.Text = item.Title ?? "Snippet";
            ShowNote(Loc.T("Değişkenler (yapıştırırken doldurulur): ") + SnippetVars.Help);
            ShowText(TextView, Cap(text));
        }
        else if (item.HasColor && item.ColorSwatch is SolidColorBrush b)
        {
            Heading.Text = Loc.T("Renk kodu");
            ColorSwatch.Background = b;
            var c = b.Color;
            ColorText.Text = $"HEX  #{c.R:X2}{c.G:X2}{c.B:X2}\nRGB  rgb({c.R}, {c.G}, {c.B})\nHSL  {Hsl(c)}";
            ColorView.Visibility = Visibility.Visible;
        }
        else if (item.IsLink)
        {
            Heading.Text = Loc.F("Link · {0}", item.Domain ?? "");
            ShowText(TextView, text.Trim());
            var clean = LinkCleaner.CleanUrl(text.Trim());
            if (clean != text.Trim() && ((App)Application.Current).Settings.LinkCleaning != "off")
                ShowText(ExtraView, Loc.T("İzleme parametreleri olmadan:") + "\n" + clean);
        }
        else if (item.IsCode)
        {
            Heading.Text = Loc.F("Kod · {0} satır", lines);
            CodeText.SetSource(CodeView, Cap(text.Replace("\t", "    ")));
            CodeView.Visibility = Visibility.Visible;
        }
        else
        {
            Heading.Text = Loc.F("Metin · {0:N0} karakter · {1} satır", text.Length, lines);
            ShowText(TextView, Cap(text));
        }
        if (text.Length > MaxChars) ShowNote(Loc.F("Uzun metin: ilk {0:N0} karakter gösteriliyor.", MaxChars));
    }

    void ShowNote(string text)
    {
        Note.Text = text;
        Note.Visibility = Visibility.Visible;
    }

    static void ShowText(System.Windows.Controls.TextBlock block, string text)
    {
        block.Text = text;
        block.Visibility = Visibility.Visible;
    }

    static string Cap(string s) => s.Length > MaxChars ? s[..MaxChars] + "…" : s;

    static string Describe(string path)
    {
        try
        {
            if (Directory.Exists(path)) return $"{Path.GetFileName(path.TrimEnd('\\'))}\n{path}\n{Loc.T("Klasör")}";
            var fi = new FileInfo(path);
            return fi.Exists
                ? $"{fi.Name}\n{fi.DirectoryName}\n{Size(fi.Length)} · {fi.LastWriteTime.ToString("g", Loc.Culture)}"
                : $"{Path.GetFileName(path)}\n{path}\n{Loc.T("Artık yok")}";
        }
        catch
        {
            return path;
        }
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:N1} MB",
    };

    static string Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }
}
