using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipDeck.Core;

internal static class ImageUtil
{
    public static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    // Decodes straight to a smaller size: a 4K screenshot shown 440 px wide would otherwise hold ~33 MB of pixels.
    public static BitmapSource DecodeToWidth(byte[] bytes, int width)
    {
        var bi = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = width;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Thumbnail(BitmapSource src, int maxW = 360, int maxH = 220)
    {
        double s = Math.Min(1.0, Math.Min((double)maxW / src.PixelWidth, (double)maxH / src.PixelHeight));
        BitmapSource t = s < 1 ? new TransformedBitmap(src, new ScaleTransform(s, s)) : src;
        return EncodePng(t);
    }

    public static ImageSource ToImageSource(byte[] bytes)
    {
        var bi = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static byte[]? PngToDib(byte[] png)
    {
        try
        {
            return Dib.FromBitmap(Decode(png));
        }
        catch (Exception ex)
        {
            Log.Error("png->dib", ex);
            return null;
        }
    }
}

internal static class Dib
{
    public static BitmapSource? ToBitmap(byte[] d)
    {
        if (d.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(d, 0);
        if (headerSize < 40 || headerSize > d.Length) return null;
        int w = BitConverter.ToInt32(d, 4);
        int rawH = BitConverter.ToInt32(d, 8);
        int bpp = BitConverter.ToUInt16(d, 14);
        int compression = BitConverter.ToInt32(d, 16);
        int colorsUsed = BitConverter.ToInt32(d, 32);
        if (bpp != 24 && bpp != 32) return null;
        if (compression != 0 && compression != 3) return null;

        bool topDown = rawH < 0;
        int h = Math.Abs(rawH);
        if (w <= 0 || h <= 0 || (long)w * h > 150_000_000) return null;

        int offset = headerSize + Math.Max(0, colorsUsed) * 4;
        if (compression == 3 && headerSize == 40) offset += 12;
        int stride = ((w * bpp + 31) / 32) * 4;
        if ((long)offset + (long)stride * h > d.Length) return null;

        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int src = offset + (topDown ? y : h - 1 - y) * stride;
            int dst = y * w * 4;
            if (bpp == 32)
            {
                Buffer.BlockCopy(d, src, px, dst, w * 4);
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    px[dst + x * 4] = d[src + x * 3];
                    px[dst + x * 4 + 1] = d[src + x * 3 + 1];
                    px[dst + x * 4 + 2] = d[src + x * 3 + 2];
                    px[dst + x * 4 + 3] = 255;
                }
            }
        }

        // Many apps write 32bpp DIBs with an all-zero alpha channel; treat those as opaque.
        if (bpp == 32)
        {
            bool hasAlpha = false;
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    public static byte[] FromBitmap(BitmapSource src)
    {
        BitmapSource b = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        b.CopyPixels(px, stride, 0);

        var d = new byte[40 + px.Length];
        var s = d.AsSpan();
        BitConverter.TryWriteBytes(s[0..], 40);
        BitConverter.TryWriteBytes(s[4..], w);
        BitConverter.TryWriteBytes(s[8..], h);
        BitConverter.TryWriteBytes(s[12..], (ushort)1);
        BitConverter.TryWriteBytes(s[14..], (ushort)32);
        BitConverter.TryWriteBytes(s[20..], px.Length);
        BitConverter.TryWriteBytes(s[24..], 3780);
        BitConverter.TryWriteBytes(s[28..], 3780);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(px, (h - 1 - y) * stride, d, 40 + y * stride, stride);
        return d;
    }
}

internal sealed class Captured
{
    public ClipKind Kind;
    public string Hash = "";
    public string? Text;
    public byte[]? Html, Rtf, Png, Thumb;
    public string[]? Files;
    public int Width, Height;
    public string? AppName;
}

internal static class Capture
{
    public static Captured? Process(RawClip raw, Crypto c)
    {
        var cap = new Captured { AppName = raw.AppName };

        if (raw.Files is not null)
        {
            cap.Kind = ClipKind.Files;
            cap.Files = raw.Files;
            cap.Hash = c.Hash("F\n" + string.Join("\n", raw.Files));
            return cap;
        }

        if (raw.Text is not null)
        {
            cap.Kind = ClipKind.Text;
            cap.Text = raw.Text;
            cap.Html = raw.Html;
            cap.Rtf = raw.Rtf;
            cap.Hash = c.Hash("T\n" + raw.Text);
            return cap;
        }

        BitmapSource? bmp = null;
        byte[]? png = null;
        if (raw.Png is not null)
        {
            try
            {
                bmp = ImageUtil.Decode(raw.Png);
                png = raw.Png;
            }
            catch (Exception ex)
            {
                Log.Error("png çözülemedi", ex);
            }
        }
        if (bmp is null && raw.Dib is not null)
        {
            bmp = Dib.ToBitmap(raw.Dib);
            if (bmp is not null) png = ImageUtil.EncodePng(bmp);
        }
        if (bmp is null || png is null) return null;

        cap.Kind = ClipKind.Image;
        cap.Png = png;
        cap.Width = bmp.PixelWidth;
        cap.Height = bmp.PixelHeight;
        cap.Thumb = ImageUtil.Thumbnail(bmp);
        cap.Hash = c.Hash(png);
        return cap;
    }
}
