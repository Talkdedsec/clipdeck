using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ClipDeck.Core;
using ClipDeck.Native;

namespace ClipDeck.UI;

internal static class Theme
{
    static string? _applied;
    public static bool IsDark { get; private set; } = true;

    public static readonly string[] AccentPresets =
    [
        "#0078D4", "#0099BC", "#00B294", "#10893E", "#7FBA00", "#FFB900",
        "#F7630C", "#E81123", "#E3008C", "#B146C2", "#8764B8", "#7A7574",
    ];

    public static void Apply(AppSettings s)
    {
        bool dark = s.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemIsDark(),
        };
        var accent = ParseColor(s.AccentColor) ?? SystemAccent(dark);
        byte card = (byte)Math.Round(Math.Clamp(s.CardOpacity, 20, 100) * 2.55);
        var key = $"{dark}|{accent}|{card}";
        if (key == _applied) return;
        _applied = key;
        IsDark = dark;

        var r = Application.Current.Resources;
        void Set(string name, string hex, byte alpha = 255)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = alpha;
            r[name] = Brush(c);
        }

        if (dark)
        {
            Set("Bg", "#202020");
            Set("Surface", "#2B2B2B");
            Set("SurfaceHover", "#333333");
            Set("SurfaceStrong", "#3A3A3A");
            Set("Card", "#2B2B2B", card);
            Set("CardHover", "#363636", card);
            Set("Field", "#1A1A1A", card);
            Set("Line", "#3D3D3D");
            Set("Text", "#F2F2F2");
            Set("TextDim", "#9E9E9E");
            Set("ScrollThumb", "#5A5A5A");
            Set("Danger", "#F0706A");
            Set("CodeKeyword", "#569CD6");
            Set("CodeString", "#CE9178");
            Set("CodeComment", "#6A9955");
            Set("CodeNumber", "#B5CEA8");
            Set("CodeTag", "#4EC9B0");
        }
        else
        {
            Set("Bg", "#F3F3F3");
            Set("Surface", "#FFFFFF");
            Set("SurfaceHover", "#F9F9F9");
            Set("SurfaceStrong", "#EBEBEB");
            Set("Card", "#FFFFFF", card);
            Set("CardHover", "#F4F4F4", card);
            Set("Field", "#FFFFFF", card);
            Set("Line", "#DDDDDD");
            Set("Text", "#1B1B1B");
            Set("TextDim", "#5F5F5F");
            Set("ScrollThumb", "#B8B8B8");
            Set("Danger", "#C42B1C");
            Set("CodeKeyword", "#0033B3");
            Set("CodeString", "#A31515");
            Set("CodeComment", "#3B7D23");
            Set("CodeNumber", "#098658");
            Set("CodeTag", "#800000");
        }

        r["Accent"] = Brush(accent);
        r["AccentText"] = Brush(Contrast(accent));
    }

    public static Color Contrast(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        return lum > 0.6 ? Colors.Black : Colors.White;
    }

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = 255;
            return c;
        }
        catch
        {
            return null;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) Win32.SetDwmInt(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, IsDark ? 1 : 0);
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return true;
        }
    }

    public static Color SystemAccent(bool dark)
    {
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(dark
                ? global::Windows.UI.ViewManagement.UIColorType.AccentLight2
                : global::Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return dark ? Color.FromRgb(0x60, 0xCD, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
        }
    }
}
