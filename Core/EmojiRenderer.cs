using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace ClipDeck.Core;

// WPF draws emoji in monochrome, so colour glyphs are rasterised with Direct2D and shown as bitmaps.
internal sealed class EmojiRenderer : IDisposable
{
    const string FontName = "Segoe UI Emoji";

    static EmojiRenderer? _shared;
    static bool _failed;

    readonly ID2D1Factory _d2d;
    readonly IDWriteFactory _dwrite;
    readonly IWICImagingFactory _wic;
    readonly Dictionary<(string, int), BitmapSource> _cache = [];
    readonly Dictionary<int, Target> _targets = [];

    sealed record Target(IWICBitmap Bitmap, ID2D1RenderTarget RenderTarget, ID2D1SolidColorBrush Brush, IDWriteTextFormat Format);

    EmojiRenderer()
    {
        _d2d = D2D1.D2D1CreateFactory<ID2D1Factory>(D2DFactoryType.SingleThreaded);
        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
        _wic = new IWICImagingFactory();
    }

    public static EmojiRenderer? Shared
    {
        get
        {
            if (_shared is null && !_failed)
            {
                try
                {
                    _shared = new EmojiRenderer();
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Log.Error("emoji çizici başlatılamadı", ex);
                }
            }
            return _shared;
        }
    }

    Target GetTarget(int px)
    {
        if (_targets.TryGetValue(px, out var t)) return t;
        var bmp = _wic.CreateBitmap((uint)px, (uint)px, WicPixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        var props = new RenderTargetProperties(new DCommonPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        var rt = _d2d.CreateWicBitmapRenderTarget(bmp, props);
        var brush = rt.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 1f));
        var fmt = _dwrite.CreateTextFormat(FontName, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, px * 0.74f, "en-us");
        fmt.TextAlignment = TextAlignment.Center;
        fmt.ParagraphAlignment = ParagraphAlignment.Center;
        fmt.WordWrapping = WordWrapping.NoWrap;
        t = new Target(bmp, rt, brush, fmt);
        _targets[px] = t;
        return t;
    }

    public BitmapSource? Render(string text, int px)
    {
        if (_cache.TryGetValue((text, px), out var cached)) return cached;
        try
        {
            var t = GetTarget(px);
            t.RenderTarget.BeginDraw();
            t.RenderTarget.Clear(new Color4(0f, 0f, 0f, 0f));
            t.RenderTarget.DrawText(text, t.Format, new Rect(0, 0, px, px), t.Brush, DrawTextOptions.EnableColorFont);
            t.RenderTarget.EndDraw();

            using var lk = t.Bitmap.Lock(BitmapLockFlags.Read);
            int stride = (int)lk.Stride;
            var src = BitmapSource.Create(px, px, 96, 96, PixelFormats.Pbgra32, null, lk.Data.DataPointer, stride * px, stride);
            src.Freeze();
            _cache[(text, px)] = src;
            return src;
        }
        catch (Exception ex)
        {
            Log.Error("emoji çizilemedi", ex);
            return null;
        }
    }

    // Emoji newer than the installed font draw as an empty box; sequences the font cannot join
    // (e.g. country flags, newer ZWJ emoji) lay out as several glyphs and come out much wider.
    public HashSet<string> FindUnsupported(IEnumerable<string> emojis)
    {
        var result = new HashSet<string>();
        using var fmt = _dwrite.CreateTextFormat(FontName, null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 100f, "en-us");
        fmt.WordWrapping = WordWrapping.NoWrap;
        using var collection = _dwrite.GetSystemFontCollection(false);
        IDWriteFont? font = null;
        if (collection.FindFamilyName(FontName, out uint index))
        {
            using var family = collection.GetFontFamily(index);
            font = family.GetFirstMatchingFont(FontWeight.Normal, FontStretch.Normal, FontStyle.Normal);
        }

        var widths = new Dictionary<string, float>();
        float Width(string s)
        {
            if (widths.TryGetValue(s, out float w)) return w;
            using var layout = _dwrite.CreateTextLayout(s, fmt, 10000f, 1000f);
            return widths[s] = layout.Metrics.WidthIncludingTrailingWhitespace;
        }

        static bool IsJoiner(int cp) =>
            cp is 0x200D or 0xFE0E or 0xFE0F or 0x20E3 || cp is >= 0xE0020 and <= 0xE007F || cp is >= 0x1F3FB and <= 0x1F3FF;

        static List<string> Parts(string s)
        {
            var parts = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                int cp = char.ConvertToUtf32(s, i);
                if (char.IsHighSurrogate(s[i])) i++;
                if (!IsJoiner(cp)) parts.Add(char.ConvertFromUtf32(cp));
            }
            return parts;
        }

        bool HasGlyphs(List<string> parts) =>
            font is null || parts.All(p => font.HasCharacter((uint)char.ConvertToUtf32(p, 0)));

        static bool IsTagFlag(string s) => s.Contains("\U000E007F");
        static bool IsRegionalPair(List<string> parts) =>
            parts.Count == 2 && parts.All(p => char.ConvertToUtf32(p, 0) is >= 0x1F1E6 and <= 0x1F1FF);

        try
        {
            float reference = Width("\U0001F600");
            bool countryFlagsMissing = false;
            foreach (var e in emojis)
            {
                var parts = Parts(e);
                float w = Width(e);
                // A joined sequence is about one emoji wide; one the font cannot join is as wide as its parts.
                bool notJoined = parts.Count >= 2 && w >= 0.9f * parts.Sum(Width);
                if (!HasGlyphs(parts) || w > reference * 1.4f || notJoined)
                {
                    result.Add(e);
                    if (IsRegionalPair(parts)) countryFlagsMissing = true;
                }
            }
            // Subdivision flags (England, Scotland, Wales) only fall back to a plain black flag, so drop them with the country flags.
            if (countryFlagsMissing)
                foreach (var e in emojis)
                    if (IsTagFlag(e)) result.Add(e);
        }
        finally
        {
            font?.Dispose();
        }
        return result;
    }

    public void ClearCache()
    {
        foreach (var t in _targets.Values)
        {
            t.Format.Dispose();
            t.Brush.Dispose();
            t.RenderTarget.Dispose();
            t.Bitmap.Dispose();
        }
        _targets.Clear();
        _cache.Clear();
    }

    public void Dispose()
    {
        ClearCache();
        _wic.Dispose();
        _dwrite.Dispose();
        _d2d.Dispose();
    }
}
