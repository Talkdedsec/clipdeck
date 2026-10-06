using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ClipDeck.Core;

// Uses the OCR engine built into Windows; languages follow the user's installed language packs.
internal static class OcrService
{
    public static async Task<string?> RecognizeAsync(byte[] png)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform();
        uint max = OcrEngine.MaxImageDimension;
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            double s = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            transform.ScaledWidth = (uint)(decoder.PixelWidth * s);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * s);
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        var text = string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
        return text.Length == 0 ? null : text;
    }
}
