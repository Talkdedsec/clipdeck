using System.Security.Cryptography;
using ClipDeck.Core;

namespace ClipDeck.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipdeck-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class CryptoTests
{
    [Fact]
    public void RoundTripsAndDetectsTampering()
    {
        using var dir = new TempDir();
        var c = new Crypto(dir.File("anahtar.bin"));
        var data = c.EncText("gizli metin çğış")!;
        Assert.Equal("gizli metin çğış", c.DecText(data));
        data[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => c.DecText(data));
    }

    [Fact]
    public void KeyIsReusedAcrossInstances()
    {
        using var dir = new TempDir();
        var first = new Crypto(dir.File("anahtar.bin"));
        var second = new Crypto(dir.File("anahtar.bin"));
        Assert.Equal("x", second.DecText(first.EncText("x")));
        Assert.Equal(first.Hash("aynı"), second.Hash("aynı"));
        Assert.NotEqual(first.Hash("aynı"), first.Hash("farklı"));
    }

    [Fact]
    public void StoredKeyIsNotPlain()
    {
        using var dir = new TempDir();
        _ = new Crypto(dir.File("anahtar.bin"));
        Assert.True(new FileInfo(dir.File("anahtar.bin")).Length > 32);
    }
}

public class BackupTests
{
    const string Password = "doğru-parola-123";
    const int HeaderSize = 16 + 4 + Backup.SaltSize + Backup.PrefixSize;

    static List<BackupItem> Sample() =>
    [
        new BackupItem { Kind = 0, Text = "merhaba", Html = [60, 98, 62], Used = 10, Created = 10, App = "Not Defteri" },
        new BackupItem { Kind = 1, Image = [1, 2, 3], Thumb = [4], Width = 1, Height = 1, Used = 11, Created = 11 },
        new BackupItem { Kind = 0, Snippet = true, Title = "imza", Text = "Saygılar,\n{imleç}", Used = 12, Created = 12 },
        new BackupItem { Kind = 2, Files = [@"C:\a.txt", @"C:\b c.pdf"], Used = 13, Created = 13 },
    ];

    static List<BackupItem> ReadAll(string path, string password)
    {
        using var reader = Backup.Open(path, password);
        return reader.Items().ToList();
    }

    // Random bytes do not compress, so these span several 1 MiB chunks.
    static List<BackupItem> Large()
    {
        var rnd = new Random(7);
        return Enumerable.Range(0, 3).Select(i =>
        {
            var img = new byte[1_500_000 + i];
            rnd.NextBytes(img);
            return new BackupItem { Kind = 1, Image = img, Width = 10, Height = 10, Used = i, Created = i };
        }).ToList();
    }

    [Fact]
    public void RoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Assert.Equal(4, Backup.Write(path, Sample(), Password));
        var read = ReadAll(path, Password);
        Assert.Equal(4, read.Count);
        Assert.Equal("merhaba", read[0].Text);
        Assert.Equal(new byte[] { 60, 98, 62 }, read[0].Html);
        Assert.Null(read[0].Rtf);
        Assert.Equal("Not Defteri", read[0].App);
        Assert.Equal(new byte[] { 1, 2, 3 }, read[1].Image);
        Assert.Equal(new byte[] { 4 }, read[1].Thumb);
        Assert.True(read[2].Snippet);
        Assert.Equal("Saygılar,\n{imleç}", read[2].Text);
        Assert.Equal(new[] { @"C:\a.txt", @"C:\b c.pdf" }, read[3].Files);
    }

    [Fact]
    public void LargeBackupSpansChunks()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        var items = Large();
        Backup.Write(path, items, Password);
        Assert.True(new FileInfo(path).Length > 4 * Backup.ChunkSize);
        var read = ReadAll(path, Password);
        Assert.Equal(items.Count, read.Count);
        for (int i = 0; i < items.Count; i++) Assert.Equal(items[i].Image, read[i].Image);
    }

    [Fact]
    public void EmptyBackupRoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Assert.Equal(0, Backup.Write(path, [], Password));
        Assert.Empty(ReadAll(path, Password));
    }

    [Fact]
    public void FileCutAtAChunkBoundaryIsDetected()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Backup.Write(path, Large(), Password);
        using (var fs = new FileStream(path, FileMode.Open))
            fs.SetLength(HeaderSize + 2L * (Backup.ChunkSize + Backup.TagSize));
        Assert.Throws<InvalidDataException>(() => ReadAll(path, Password));
    }

    [Fact]
    public void TamperedChunkIsDetected()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Backup.Write(path, Large(), Password);
        var bytes = File.ReadAllBytes(path);
        bytes[HeaderSize + Backup.ChunkSize + 100] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<CryptographicException>(() => ReadAll(path, Password));
    }

    [Fact]
    public void FailedWriteLeavesNoFile()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        static IEnumerable<BackupItem> Broken()
        {
            yield return new BackupItem { Kind = 0, Text = "ilk" };
            throw new IOException("disk dolu");
        }
        Assert.Throws<IOException>(() => Backup.Write(path, Broken(), Password));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Version1BackupStillOpens()
    {
        using var dir = new TempDir();
        var path = dir.File("eski.clipdeck");
        WriteVersion1(path, Sample(), Password);
        var read = ReadAll(path, Password);
        Assert.Equal(4, read.Count);
        Assert.Equal("merhaba", read[0].Text);
        Assert.Equal(new byte[] { 1, 2, 3 }, read[1].Image);
        Assert.Throws<CryptographicException>(() => ReadAll(path, "yanlış-parola"));
    }

    // The format written by version 0.2.0: one AES-GCM block over the gzipped JSON document.
    static void WriteVersion1(string path, List<BackupItem> items, string password)
    {
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                System.Text.Json.JsonSerializer.Serialize(gz, new BackupFile { Created = 1, Items = items });
            packed = ms.ToArray();
        }
        var magic = System.Text.Encoding.ASCII.GetBytes("CLIPDECK-YEDEK-1");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, 10_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[packed.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, packed, cipher, tag, magic);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(magic);
        w.Write(10_000);
        w.Write(salt);
        w.Write(nonce);
        w.Write(tag);
        w.Write(cipher);
    }

    [Fact]
    public void WrongPasswordFails()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Backup.Write(path, Sample(), Password);
        Assert.Throws<CryptographicException>(() => Backup.Open(path, "yanlış-parola"));
    }

    [Fact]
    public void ForeignFileIsRejected()
    {
        using var dir = new TempDir();
        var path = dir.File("baska.clipdeck");
        File.WriteAllText(path, "bu bir yedek değil, sadece uzun bir metin dosyası");
        Assert.Throws<InvalidDataException>(() => Backup.Open(path, "x"));
        File.WriteAllText(path, "kısa");
        Assert.Throws<InvalidDataException>(() => Backup.Open(path, "x"));
    }

    [Fact]
    public void ContentIsNotReadableInTheFile()
    {
        using var dir = new TempDir();
        var path = dir.File("yedek.clipdeck");
        Backup.Write(path, Sample(), Password);
        var raw = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.DoesNotContain("merhaba", raw);
        Assert.DoesNotContain("imza", raw);
    }
}

public class SchemaMigrationTests
{
    [Fact]
    public void Version1DatabaseMovesBlobsOut()
    {
        using var dir = new TempDir();
        var crypto = new Crypto(dir.File("anahtar.bin"));
        var dbPath = dir.File("gecmis.db");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items(id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, created INTEGER NOT NULL, used INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0, snippet INTEGER NOT NULL DEFAULT 0, hash TEXT NOT NULL,
                    source BLOB, title BLOB, text BLOB, html BLOB, rtf BLOB, image BLOB, thumb BLOB, ocr BLOB, files BLOB,
                    width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0, length INTEGER NOT NULL DEFAULT 0);
                INSERT INTO items(kind, created, used, hash, text, html, image, thumb, width, height)
                VALUES (1, 1, 1, 'h1', NULL, NULL, $img, $thumb, 4, 3), (0, 2, 2, 'h2', $text, $html, NULL, NULL, 0, 0);
                PRAGMA user_version=1;
                """;
            cmd.Parameters.AddWithValue("$img", crypto.EncBytes([9, 8, 7]));
            cmd.Parameters.AddWithValue("$thumb", crypto.EncBytes([1]));
            cmd.Parameters.AddWithValue("$text", crypto.EncText("eski metin"));
            cmd.Parameters.AddWithValue("$html", crypto.EncBytes([60, 98, 62]));
            cmd.ExecuteNonQuery();
        }

        using var store = new ClipStore(dbPath, crypto);
        var items = store.LoadAll();
        Assert.Equal(2, items.Count);
        var image = items.Single(i => i.IsImage);
        Assert.Equal(new byte[] { 9, 8, 7 }, store.LoadPayload(image.Id).Image);
        Assert.Equal(new byte[] { 1 }, store.LoadThumb(image.Id));
        var text = items.Single(i => i.IsText);
        var payload = store.LoadPayload(text.Id);
        Assert.Equal("eski metin", payload.Text);
        Assert.Equal(new byte[] { 60, 98, 62 }, payload.Html);

        store.Delete([image.Id]);
        Assert.Null(store.LoadThumb(image.Id));
    }
}

public class StoreAndManagerTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Dir { get; } = new();
        public Crypto Crypto { get; }
        public ClipStore Store { get; }
        public AppSettings Settings { get; } = new();
        public ClipManager Manager { get; }

        public Fixture()
        {
            Crypto = new Crypto(Dir.File("anahtar.bin"));
            Store = new ClipStore(Dir.File("gecmis.db"), Crypto);
            Manager = new ClipManager(Store, Crypto, Settings);
            Manager.Load();
        }

        public void Dispose()
        {
            Store.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public void AddsAndDeduplicates()
    {
        using var f = new Fixture();
        f.Manager.AddText("bir", null);
        f.Manager.AddText("iki", null);
        f.Manager.AddText("bir", null);
        Assert.Equal(2, f.Manager.Items.Count);
        Assert.Equal("bir", f.Manager.Items[0].Preview);
    }

    [Fact]
    public void PersistsEncryptedAndReloads()
    {
        using var f = new Fixture();
        var it = f.Manager.AddText("kalıcı metin", "Not Defteri");
        f.Manager.SetPinned(it, true);
        f.Manager.AddSnippet("imza", "Saygılar");
        f.Manager.Load();
        Assert.Equal(2, f.Manager.Items.Count);
        Assert.Contains(f.Manager.Items, i => i.Pinned && i.AppName == "Not Defteri");
        Assert.Contains(f.Manager.Items, i => i.IsSnippet && i.Title == "imza");

        foreach (var name in new[] { "gecmis.db", "gecmis.db-wal" })
        {
            if (!File.Exists(f.Dir.File(name))) continue;
            using var fs = new FileStream(f.Dir.File(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            Assert.DoesNotContain("kalıcı metin", text);
            Assert.DoesNotContain("Not Defteri", text);
        }
    }

    [Fact]
    public void TrimKeepsPinnedAndSnippets()
    {
        using var f = new Fixture();
        var pinned = f.Manager.AddText("sabit", null);
        f.Manager.SetPinned(pinned, true);
        f.Manager.AddSnippet("s", "snippet");
        for (int i = 0; i < 10; i++) f.Manager.AddText("öğe " + i, null);
        f.Settings.MaxItems = 3;
        f.Manager.Trim();
        Assert.Equal(3, f.Manager.Items.Count(i => !i.Pinned && !i.IsSnippet));
        Assert.Contains(pinned, f.Manager.Items);
        Assert.Contains(f.Manager.Items, i => i.IsSnippet);
    }

    [Fact]
    public async Task TrimByAgeDropsOldUnpinnedOnly()
    {
        using var f = new Fixture();
        long old = TextUtil.Now() - 10L * 86_400_000;
        var path = f.Dir.File("yedek.clipdeck");
        Backup.Write(path,
        [
            new BackupItem { Kind = 0, Text = "eski", Used = old, Created = old },
            new BackupItem { Kind = 0, Text = "eski sabit", Used = old, Created = old, Pinned = true },
        ], "parola");
        await f.Manager.ImportAsync(path, "parola");
        f.Manager.AddText("yeni", null);
        f.Settings.MaxAgeDays = 7;
        f.Manager.Trim();
        Assert.DoesNotContain(f.Manager.Items, i => i.Preview == "eski");
        Assert.Contains(f.Manager.Items, i => i.Preview == "eski sabit");
        Assert.Contains(f.Manager.Items, i => i.Preview == "yeni");
    }

    [Fact]
    public async Task ExportImportSkipsDuplicates()
    {
        using var a = new Fixture();
        a.Manager.AddText("ortak", null);
        a.Manager.AddText("sadece a", "Not Defteri");
        a.Manager.AddSnippet("imza", "Saygılar");
        var path = a.Dir.File("yedek.clipdeck");
        Assert.Equal(3, await a.Manager.ExportAsync(path, "parola"));

        using var b = new Fixture();
        b.Manager.AddText("ortak", null);
        var (added, skipped) = await b.Manager.ImportAsync(path, "parola");
        Assert.Equal(2, added);
        Assert.Equal(1, skipped);
        Assert.Equal(3, b.Manager.Items.Count);
        Assert.Contains(b.Manager.Items, i => i.AppName == "Not Defteri" && i.Preview == "sadece a");
        Assert.Contains(b.Manager.Items, i => i.IsSnippet && i.Title == "imza");

        var again = await b.Manager.ImportAsync(path, "parola");
        Assert.Equal(0, again.added);
    }

    [Fact]
    public async Task ImportCommitsManyItemsInBatches()
    {
        using var f = new Fixture();
        var path = f.Dir.File("yedek.clipdeck");
        Backup.Write(path, Enumerable.Range(0, 450).Select(i => new BackupItem { Kind = 0, Text = "öğe " + i, Used = i, Created = i }), "parola");
        var (added, _) = await f.Manager.ImportAsync(path, "parola");
        Assert.Equal(450, added);
        Assert.Equal(450, f.Manager.Items.Count);
        Assert.Equal("öğe 449", f.Manager.Items[0].Preview);
    }

    [Fact]
    public void SensitiveItemsAreMaskedInPreviewAndSearch()
    {
        using var f = new Fixture();
        var it = f.Manager.AddText("4111 1111 1111 1111", null);
        Assert.True(it.IsSensitive);
        Assert.Equal("•••• •••• •••• 1111", it.Preview);
        Assert.DoesNotContain("4111", it.SearchIndex);
    }
}
