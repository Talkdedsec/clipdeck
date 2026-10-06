using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace ClipDeck.Core;

public enum ClipKind { Text = 0, Image = 1, Files = 2 }

public enum PasteMode { Normal, Plain }

public sealed class ClipItem : INotifyPropertyChanged
{
    public static bool MaskSensitive { get; set; } = true;

    public long Id { get; set; }
    public ClipKind Kind { get; init; }
    public long Created { get; init; }
    public long Used { get; set; }
    public string Hash { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int Length { get; set; }
    public bool IsSnippet { get; init; }
    public string? AppName { get; set; }
    public string? OcrText { get; set; }
    internal string SearchIndex { get; private set; } = "";
    internal Func<ClipItem, ImageSource?>? ThumbLoader { get; set; }

    public SensitiveKind Sensitivity { get; private set; }
    public bool IsSensitive => Sensitivity != SensitiveKind.None;
    public bool IsLink { get; private set; }
    public string? Domain { get; private set; }
    public Brush? ColorSwatch { get; private set; }
    public bool HasColor => ColorSwatch is not null;
    public bool IsCode { get; private set; }

    string? _title;
    public string? Title
    {
        get => _title;
        set { _title = value; Notify(); Notify(nameof(HasTitle)); }
    }

    string _preview = "";
    public string Preview
    {
        get => _preview;
        set { _preview = value; Notify(); }
    }

    bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; Notify(); }
    }

    bool _checked;
    public bool IsChecked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Notify(); Notify(nameof(ShowQuickIndex)); } }
    }

    string _quickIndex = "";
    public string QuickIndex
    {
        get => _quickIndex;
        set { if (_quickIndex != value) { _quickIndex = value; Notify(); } }
    }

    public bool ShowQuickIndex => !_checked;

    ImageSource? _thumb;
    bool _thumbTried;
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbTried)
            {
                _thumbTried = true;
                try { _thumb = ThumbLoader?.Invoke(this); }
                catch (Exception ex) { Log.Error("küçük resim", ex); }
            }
            return _thumb;
        }
    }

    public void ReleaseThumbnail()
    {
        _thumb = null;
        _thumbTried = false;
    }

    public bool HasTitle => !string.IsNullOrWhiteSpace(_title);
    public bool IsText => Kind == ClipKind.Text;
    public bool IsImage => Kind == ClipKind.Image;
    public bool IsFiles => Kind == ClipKind.Files;
    public bool ShowText => Kind != ClipKind.Image;
    public bool ShowPlain => ShowText && !IsCode;
    public bool ShowCode => IsCode;

    public string KindGlyph => IsSnippet ? ""
        : IsSensitive && MaskSensitive ? ""
        : Kind switch
        {
            ClipKind.Image => "",
            ClipKind.Files => "",
            _ when IsLink => "",
            _ when HasColor => "",
            _ when IsCode => "",
            _ => "",
        };

    public string Meta
    {
        get
        {
            var parts = new List<string>(4);
            if (IsSnippet) parts.Add("Snippet");
            else
            {
                if (IsLink && Domain is not null) parts.Add(Domain);
                if (!string.IsNullOrEmpty(AppName)) parts.Add(AppName);
            }
            if (IsSensitive && MaskSensitive) parts.Add(Loc.T("gizli"));
            parts.Add(TextUtil.Relative(Used));
            switch (Kind)
            {
                case ClipKind.Image when Width > 0:
                    parts.Add($"{Width}×{Height}");
                    break;
                case ClipKind.Files:
                    parts.Add(string.Format(Loc.T("{0} öğe"), Length));
                    break;
                case ClipKind.Text when Length > 300 && !IsSensitive:
                    parts.Add(string.Format(Loc.T("{0:N0} karakter"), Length));
                    break;
            }
            return string.Join(" · ", parts);
        }
    }

    public void RefreshMeta() => Notify(nameof(Meta));

    public void SetContent(string? text, string[]? files)
    {
        if (Kind == ClipKind.Files && files is not null)
        {
            Length = files.Length;
            Preview = TextUtil.FilesPreview(files);
            RebuildIndex(string.Join("\n", files));
            return;
        }

        if (Kind == ClipKind.Text) Length = text?.Length ?? 0;
        Sensitivity = Kind == ClipKind.Text && !IsSnippet && text is not null ? Sensitive.Detect(text) : SensitiveKind.None;
        if (IsSensitive && MaskSensitive)
        {
            IsLink = IsCode = false;
            ColorSwatch = null;
            Preview = Sensitive.Mask(text!, Sensitivity);
            RebuildIndex(null);
            return;
        }

        var trimmed = text?.Trim() ?? "";
        ColorSwatch = Kind == ClipKind.Text ? TextUtil.ParseColor(trimmed) : null;
        (IsLink, Domain) = Kind == ClipKind.Text ? TextUtil.ParseLink(trimmed) : (false, null);
        IsCode = Kind == ClipKind.Text && !IsLink && ColorSwatch is null && text is not null && TextUtil.LooksLikeCode(text);
        Preview = text is null ? "" : IsCode ? TextUtil.CodePreview(text) : TextUtil.Preview(text);
        RebuildIndex(text);
    }

    public void RebuildIndex(string? text)
    {
        const int cap = 8000;
        var sb = new StringBuilder();
        if (_title is not null) sb.Append(_title).Append('\n');
        if (IsSensitive && MaskSensitive) sb.Append(Loc.T("gizli")).Append('\n');
        else if (text is not null) sb.Append(text.Length > cap ? text[..cap] : text).Append('\n');
        if (OcrText is not null) sb.Append(OcrText.Length > cap ? OcrText[..cap] : OcrText).Append('\n');
        if (AppName is not null) sb.Append(AppName);
        SearchIndex = TextUtil.Normalize(sb.ToString());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static partial class TextUtil
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Case and Turkish-diacritic insensitive form so "sifre" finds "Şifre" and "ISIK" finds "ışık".
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' or 'î' or 'Î' => 'i',
                'Ş' or 'ş' => 's',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' or 'û' or 'Û' => 'u',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                'â' or 'Â' => 'a',
                '\r' or '\n' or '\t' => ' ',
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }

    public static string Preview(string s)
    {
        var t = s.Length > 600 ? s[..600] : s;
        t = t.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Trim();
        return ManyNewlines().Replace(t, "\n\n");
    }

    public static string CodePreview(string s)
    {
        var lines = (s.Length > 2000 ? s[..2000] : s).Replace("\r\n", "\n").Replace("\t", "    ").Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace).Take(8).ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..].TrimEnd() : l.TrimEnd()));
    }

    public static string FilesPreview(string[] files)
    {
        var names = files.Take(4).Select(p =>
        {
            var n = Path.GetFileName(p.TrimEnd('\\'));
            return string.IsNullOrEmpty(n) ? p : n;
        });
        var s = string.Join("\n", names);
        if (files.Length > 4) s += $"\n… +{files.Length - 4}";
        return s;
    }

    public static (bool, string?) ParseLink(string t)
    {
        if (t.Length > 2000 || !LinkPattern().IsMatch(t)) return (false, null);
        var uri = t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + t : t;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return (false, null);
        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        return (true, host);
    }

    public static Brush? ParseColor(string t)
    {
        if (t.Length is < 4 or > 40) return null;
        Color? c = null;
        if (HexColor().IsMatch(t))
        {
            var hex = t[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
            if (hex.Length == 8) hex = hex[6..] + hex[..6];
            try { c = (Color)ColorConverter.ConvertFromString("#" + hex); } catch { }
        }
        else if (RgbColor().Match(t) is { Success: true } m)
        {
            byte P(int i) => (byte)Math.Clamp(int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255);
            c = Color.FromRgb(P(1), P(2), P(3));
        }
        else if (HslColor().Match(t) is { Success: true } h
            && double.TryParse(h.Groups[1].Value, CultureInfo.InvariantCulture, out double hue)
            && double.TryParse(h.Groups[2].Value, CultureInfo.InvariantCulture, out double sat)
            && double.TryParse(h.Groups[3].Value, CultureInfo.InvariantCulture, out double lig))
        {
            c = FromHsl(hue, Math.Clamp(sat / 100, 0, 1), Math.Clamp(lig / 100, 0, 1));
        }
        if (c is not { } color) return null;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = (h % 360 + 360) % 360 / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        double m = l - c / 2;
        byte B(double v) => (byte)Math.Round(Math.Clamp(v + m, 0, 1) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }

    public static bool LooksLikeCode(string t)
    {
        if (t.Length < 12 || t.Length > 200_000) return false;
        var sample = t.Length > 4000 ? t[..4000] : t;
        var trimmed = t.Trim();
        if (trimmed.Length <= 100_000 && BlockStartEnd().IsMatch(trimmed) && IsJson(trimmed)) return true;
        int score = 0;
        if (BlockStartEnd().IsMatch(sample.Trim())) score += 2;
        if (LineEndPunct().Matches(sample).Count >= 2) score += 2;
        if (CodeKeyword().IsMatch(sample)) score += 1;
        if (CodeOperator().IsMatch(sample)) score += 1;
        if (IndentedLine().IsMatch(sample)) score += 1;
        if (MarkupTag().IsMatch(sample) && sample.Contains("</")) score += 2;
        return score >= 3;
    }

    static bool IsJson(string s)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(s);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public static string Relative(long unixMs)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return Loc.T("az önce");
        if (d.TotalMinutes < 60) return string.Format(Loc.T("{0} dk önce"), (int)d.TotalMinutes);
        if (t.Date == DateTime.Today) return string.Format(Loc.T("{0} sa önce"), (int)d.TotalHours);
        if (t.Date == DateTime.Today.AddDays(-1)) return string.Format(Loc.T("dün {0}"), t.ToString("HH:mm"));
        if (d.TotalDays < 7) return t.ToString("dddd HH:mm", Loc.Culture);
        return t.ToString("d MMM yyyy", Loc.Culture);
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyNewlines();

    [GeneratedRegex(@"^(?:https?://|www\.)\S+$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+%?\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"^hsla?\(\s*([\d.]+)(?:deg)?\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%\s*(?:,\s*[\d.]+%?\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex HslColor();

    [GeneratedRegex(@"^[\{\[][\s\S]*[\}\]]$")]
    private static partial Regex BlockStartEnd();

    [GeneratedRegex(@"[;\{\}]\s*$", RegexOptions.Multiline)]
    private static partial Regex LineEndPunct();

    [GeneratedRegex(@"\b(?:function|const|let|var|def|class|public|private|static|void|return|import|using|namespace|SELECT|FROM|WHERE|INSERT|UPDATE|elif|fn|func|package|#include|struct|async|await|interface)\b")]
    private static partial Regex CodeKeyword();

    [GeneratedRegex(@"(?:=>|==|!=|&&|\|\||::|->|\+=|:=)")]
    private static partial Regex CodeOperator();

    [GeneratedRegex(@"^(?: {2,}|\t)\S", RegexOptions.Multiline)]
    private static partial Regex IndentedLine();

    [GeneratedRegex(@"<\w+[^>]*>")]
    private static partial Regex MarkupTag();
}
