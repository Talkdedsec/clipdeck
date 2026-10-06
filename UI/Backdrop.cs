using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ClipDeck.Core;

namespace ClipDeck.UI;

// The photo is scaled and blurred once into a static bitmap; a live BlurEffect would be recomputed on every repaint.
internal static class Backdrop
{
    static string? _key;
    static BitmapSource? _cached;

    public static void Clear()
    {
        _key = null;
        _cached = null;
    }

    public static string? PathFor(AppSettings s) =>
        string.IsNullOrEmpty(s.BackgroundImage) ? null : Path.Combine(App.DataDir, s.BackgroundImage);

    public static BitmapSource? Get(string? path, int width, int height, double blur)
    {
        if (path is null || width <= 0 || height <= 0 || !File.Exists(path)) return null;
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{width}|{height}|{blur:0.0}";
        if (key == _key) return _cached;
        _cached = Render(path, width, height, blur);
        _key = key;
        return _cached;
    }

    public static BitmapSource? Render(string path, int width, int height, double blur)
    {
        try
        {
            int pad = (int)Math.Ceiling(blur * 2);
            int boxW = width + 2 * pad, boxH = height + 2 * pad;

            int srcW, srcH;
            using (var fs = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                srcW = frame.PixelWidth;
                srcH = frame.PixelHeight;
            }
            double cover = Math.Max((double)boxW / srcW, (double)boxH / srcH);

            var img = new BitmapImage();
            using (var fs = File.OpenRead(path))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = fs;
                if (cover < 1) img.DecodePixelWidth = (int)Math.Ceiling(srcW * cover) + 2;
                img.EndInit();
            }
            img.Freeze();

            var picture = new Image
            {
                Source = img,
                Stretch = Stretch.UniformToFill,
                Width = boxW,
                Height = boxH,
                Effect = blur > 0 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } : null,
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            var host = new Canvas { Width = width, Height = height, ClipToBounds = true };
            Canvas.SetLeft(picture, -pad);
            Canvas.SetTop(picture, -pad);
            host.Children.Add(picture);
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Log.Error("arka plan", ex);
            return null;
        }
    }
}
