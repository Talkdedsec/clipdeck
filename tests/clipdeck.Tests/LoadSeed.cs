using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipDeck.Core;
using Xunit.Abstractions;

namespace ClipDeck.Tests;

// Not a regular test: fills CLIPDECK_SEED_DIR with a large history for load testing the app.
// Run: set CLIPDECK_SEED_DIR=... ; dotnet test --filter FullyQualifiedName~LoadSeed
public class LoadSeed(ITestOutputHelper output)
{
    static readonly string[] Words =
    [
        "toplantı", "rapor", "fatura", "sipariş", "müşteri", "proje", "ekip", "tarih", "bütçe", "sunum", "ödeme", "teslim",
        "güncelleme", "hata", "sürüm", "kod", "tasarım", "plan", "adres", "telefon", "ekim", "kasım", "pazartesi", "cuma",
        "invoice", "meeting", "release", "server", "deploy", "backup", "review", "draft", "final", "update",
    ];

    [Fact]
    public void Seed()
    {
        var dir = Environment.GetEnvironmentVariable("CLIPDECK_SEED_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        var rnd = new Random(42);
        var crypto = new Crypto(Path.Combine(dir, "anahtar.bin"));
        using var store = new ClipStore(Path.Combine(dir, "gecmis.db"), crypto);
        long now = TextUtil.Now();
        long Ago() => now - (long)(rnd.NextDouble() * 90 * 86_400_000);
        string Sentence(int words) => string.Join(" ", Enumerable.Range(0, words).Select(_ => Words[rnd.Next(Words.Length)]));

        store.InTransaction(() =>
        {
            for (int i = 0; i < 10_000; i++)
            {
                int kind = rnd.Next(100);
                string text = kind switch
                {
                    < 55 => Sentence(rnd.Next(3, 25)),
                    < 70 => $"https://ornek{rnd.Next(500)}.com/sayfa/{i}?id={i}&utm_source=test",
                    < 80 => $"function f{i}(a, b) {{\n    // {Sentence(4)}\n    return a + b * {i};\n}}",
                    < 85 => $"#{rnd.Next(0x1000000):X6}",
                    < 95 => string.Join("\n", Enumerable.Range(0, rnd.Next(20, 80)).Select(_ => Sentence(12))),
                    _ => $"{Sentence(3)} {i} " + new string('x', rnd.Next(10)),
                };
                Insert(store, crypto, ClipKind.Text, Ago(), text: text, app: rnd.Next(3) == 0 ? "Google Chrome" : "Visual Studio Code");
            }
            for (int i = 0; i < 50; i++)
                Insert(store, crypto, ClipKind.Files, Ago(), files: [$@"C:\Belgeler\dosya{i}.pdf", $@"C:\Belgeler\ek{i}.xlsx"], app: "Windows Gezgini");
            for (int i = 0; i < 30; i++)
                Insert(store, crypto, ClipKind.Text, Ago(), text: $"Snippet {i}: {Sentence(10)} {{tarih}}", snippet: true, title: $"Şablon {i}");
        });

        for (int i = 0; i < 300; i++)
        {
            int w = 1280, h = 720;
            var px = new byte[w * h * 4];
            rnd.NextBytes(px);
            for (int p = 3; p < px.Length; p += 4) px[p] = 255;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            var png = ImageUtil.EncodePng(bmp);
            Insert(store, crypto, ClipKind.Image, Ago(), png: png, thumb: ImageUtil.Thumbnail(bmp), width: w, height: h, app: "Ekran Alıntısı Aracı");
        }
        output.WriteLine($"üretildi: {sw.Elapsed.TotalSeconds:0.0} sn, {new FileInfo(Path.Combine(dir, "gecmis.db")).Length / 1024 / 1024} MB");
    }

    // A small, realistic history for screenshots (README and site).
    // Run: set CLIPDECK_DEMO_DIR=... ; dotnet test --filter FullyQualifiedName~LoadSeed.DemoSeed
    [Fact]
    public void DemoSeed()
    {
        var dir = Environment.GetEnvironmentVariable("CLIPDECK_DEMO_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var crypto = new Crypto(Path.Combine(dir, "anahtar.bin"));
        using var store = new ClipStore(Path.Combine(dir, "gecmis.db"), crypto);
        long now = TextUtil.Now();
        long Ago(double minutes) => now - (long)(minutes * 60_000);
        var (png, thumb) = OnSta(DemoImage);

        Insert(store, crypto, ClipKind.Text, Ago(0.2), text: "https://github.com/dotnet/wpf", app: "Google Chrome");
        Insert(store, crypto, ClipKind.Text, Ago(3), text: """
            public async Task<int> CountAsync(string path)
            {
                var lines = await File.ReadAllLinesAsync(path);
                return lines.Count(l => l.Length > 0);
            }
            """, app: "Visual Studio Code");
        Insert(store, crypto, ClipKind.Image, Ago(9), png: png, thumb: thumb, width: 1280, height: 720, app: "Ekran Alıntısı Aracı",
            ocr: "Haftalık rapor — Ekim\nSatış +18%");
        Insert(store, crypto, ClipKind.Text, Ago(14), text: "#7C5CFF", app: "Figma");
        Insert(store, crypto, ClipKind.Text, Ago(26), text: "Toplantı yarın 14:00'te, B salonunda. Sunumu ve bütçe tablosunu getirmeyi unutma.", app: "Not Defteri");
        Insert(store, crypto, ClipKind.Files, Ago(41), files: [@"C:\Belgeler\Sunum.pptx", @"C:\Belgeler\Bütçe 2026.xlsx"], app: "Windows Gezgini");
        Insert(store, crypto, ClipKind.Text, Ago(65), text: "4111 1111 1111 1111", app: "Not Defteri");
        Insert(store, crypto, ClipKind.Text, Ago(130), text: """{ "ad": "clipdeck", "sürüm": "0.3.0", "platform": "windows" }""", app: "Visual Studio Code");
        Insert(store, crypto, ClipKind.Text, Ago(300), text: "Kargo takip no: TR 4821 0937 55", app: "Google Chrome", pinned: true);
        Insert(store, crypto, ClipKind.Text, Ago(1), text: "Saygılarımla,\nAli Yılmaz\n{tarih}", snippet: true, title: "E-posta imzası");
        Insert(store, crypto, ClipKind.Text, Ago(2), text: "Merhaba {imleç},\n\nToplantı notlarını ekte bulabilirsin.", snippet: true, title: "Toplantı notu");
        output.WriteLine("tanıtım verisi hazır: " + dir);
    }

    internal static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    // A simple bar chart, so the image card and OCR preview have something real to show.
    static (byte[] png, byte[] thumb) DemoImage()
    {
        const int w = 1280, h = 720;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1E, 0x29, 0x4B), Color.FromRgb(0x3B, 0x2A, 0x6E), 30), null, new System.Windows.Rect(0, 0, w, h));
            var face = new Typeface("Segoe UI Semibold");
            dc.DrawText(new FormattedText("Haftalık rapor — Ekim", System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, face, 64, Brushes.White, 1.0), new System.Windows.Point(80, 70));
            dc.DrawText(new FormattedText("Satış +18%", System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, face, 40, new SolidColorBrush(Color.FromRgb(0x9E, 0xF0, 0xB8)), 1.0), new System.Windows.Point(80, 160));
            int[] bars = [180, 240, 210, 300, 280, 360, 420];
            for (int i = 0; i < bars.Length; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF)), null,
                    new System.Windows.Rect(100 + i * 160, 660 - bars[i], 100, bars[i]), 10, 10);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return (ImageUtil.EncodePng(bmp), ImageUtil.Thumbnail(bmp));
    }

    // Turns a seeded database back into the version 1 layout, to time the one-off upgrade on a big history.
    [Fact]
    public void DowngradeToVersion1()
    {
        var dir = Environment.GetEnvironmentVariable("CLIPDECK_DOWNGRADE_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "gecmis.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            BEGIN;
            CREATE TABLE items_v1(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, created INTEGER NOT NULL, used INTEGER NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0, snippet INTEGER NOT NULL DEFAULT 0, hash TEXT NOT NULL,
                source BLOB, title BLOB, text BLOB, html BLOB, rtf BLOB, image BLOB, thumb BLOB, ocr BLOB, files BLOB,
                width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, length INTEGER NOT NULL DEFAULT 0);
            INSERT INTO items_v1 SELECT i.id, i.kind, i.created, i.used, i.pinned, i.snippet, i.hash, i.source, i.title, i.text,
                p.html, p.rtf, p.image, p.thumb, i.ocr, i.files, i.width, i.height, i.length
                FROM items i LEFT JOIN payloads p ON p.id = i.id;
            DROP TABLE items;
            DROP TABLE payloads;
            ALTER TABLE items_v1 RENAME TO items;
            PRAGMA user_version=1;
            COMMIT;
            VACUUM;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        output.WriteLine("v1 biçimine çevrildi");
    }

    // Backs up a big history and restores it into an empty one, reporting time and peak memory.
    // Run: set CLIPDECK_BACKUP_DIR=<seeded data dir> ; dotnet test --filter FullyQualifiedName~LoadSeed.BackupBig
    [Fact]
    public async Task BackupBig()
    {
        var dir = Environment.GetEnvironmentVariable("CLIPDECK_BACKUP_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        using var temp = new TempDir();
        var file = temp.File("buyuk.clipdeck");
        var crypto = new Crypto(Path.Combine(dir, "anahtar.bin"));
        using var store = new ClipStore(Path.Combine(dir, "gecmis.db"), crypto);
        var source = new ClipManager(store, crypto, new AppSettings());
        source.Load();
        var proc = Process.GetCurrentProcess();
        long baseline = proc.WorkingSet64;

        var sw = Stopwatch.StartNew();
        int count = await source.ExportAsync(file, "yuk-testi");
        proc.Refresh();
        output.WriteLine($"yedek: {count} öğe, {sw.Elapsed.TotalSeconds:0.0} sn, {new FileInfo(file).Length / 1024 / 1024} MB, en yüksek bellek {proc.PeakWorkingSet64 / 1024 / 1024} MB (başta {baseline / 1024 / 1024} MB)");

        var targetCrypto = new Crypto(temp.File("anahtar.bin"));
        using var targetStore = new ClipStore(temp.File("gecmis.db"), targetCrypto);
        var target = new ClipManager(targetStore, targetCrypto, new AppSettings());
        target.Load();
        sw.Restart();
        var (added, skipped) = await target.ImportAsync(file, "yuk-testi");
        proc.Refresh();
        output.WriteLine($"geri yükleme: {added} eklendi, {skipped} atlandı, {sw.Elapsed.TotalSeconds:0.0} sn, en yüksek bellek {proc.PeakWorkingSet64 / 1024 / 1024} MB");
        // The seeder writes straight to the store, so random repeats it produced are skipped here as duplicates.
        Assert.Equal(count, added + skipped);
        Assert.Equal(added, target.Items.Count);
    }

    static void Insert(ClipStore store, Crypto crypto, ClipKind kind, long used, string? text = null, string[]? files = null,
        byte[]? png = null, byte[]? thumb = null, int width = 0, int height = 0, string? app = null, bool snippet = false, string? title = null,
        string? ocr = null, bool pinned = false)
    {
        var hash = snippet ? crypto.Hash("S\n" + Guid.NewGuid())
            : kind == ClipKind.Image ? crypto.Hash(png!)
            : kind == ClipKind.Files ? crypto.Hash("F\n" + string.Join("\n", files!))
            : crypto.Hash("T\n" + text);
        var item = new ClipItem
        {
            Kind = kind, Created = used, Used = used, Hash = hash, Width = width, Height = height,
            AppName = app, IsSnippet = snippet, Length = text?.Length ?? files?.Length ?? 0, OcrText = ocr,
        };
        item.Title = title;
        item.Pinned = pinned;
        store.Insert(item, text, null, null, png, thumb, files);
    }
}
