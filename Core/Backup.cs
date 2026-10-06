using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipDeck.Core;

internal sealed class BackupItem
{
    public int Kind { get; set; }
    public long Created { get; set; }
    public long Used { get; set; }
    public bool Pinned { get; set; }
    public bool Snippet { get; set; }
    public string? Title { get; set; }
    public string? App { get; set; }
    public string? Ocr { get; set; }
    public string? Text { get; set; }
    public byte[]? Html { get; set; }
    public byte[]? Rtf { get; set; }
    public byte[]? Image { get; set; }
    public byte[]? Thumb { get; set; }
    public string[]? Files { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    internal BackupItem WithoutBlobs()
    {
        var copy = (BackupItem)MemberwiseClone();
        copy.Html = copy.Rtf = copy.Image = copy.Thumb = null;
        return copy;
    }
}

// Version 1 layout: the whole backup as one JSON document.
internal sealed class BackupFile
{
    public int Version { get; set; } = 1;
    public long Created { get; set; }
    public List<BackupItem> Items { get; set; } = [];
}

internal sealed class BackupHeader
{
    public int Version { get; set; } = 2;
    public long Created { get; set; }
}

// The history key is bound to this Windows account, so backups use their own password-derived key instead.
// Version 2 is a stream: records are compressed, then sealed in 1 MiB AES-GCM chunks, so a history holding
// gigabytes of screenshots never has to fit in memory (version 1 did, and failed past 2 GB).
internal static class Backup
{
    public const string Extension = ".clipdeck";
    static readonly byte[] MagicV1 = Encoding.ASCII.GetBytes("CLIPDECK-YEDEK-1");
    static readonly byte[] MagicV2 = Encoding.ASCII.GetBytes("CLIPDECK-YEDEK-2");
    const int Iterations = 600_000;
    internal const int SaltSize = 16, NonceSize = 12, TagSize = 16, PrefixSize = 7;
    internal const int ChunkSize = 1 << 20;
    const int MaxRecord = 512 * 1024 * 1024;

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static string Corrupt => Loc.T("Yedek dosyası bozuk.");

    public static int Write(string path, IEnumerable<BackupItem> items, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var header = Header(Iterations, salt, prefix);
        var key = DeriveKey(password, salt, Iterations);
        // Written beside the target and moved into place at the end, so a failed backup never leaves half a file.
        var temp = path + ".yaziliyor";
        int count = 0;
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(header);
                using var gz = new GZipStream(new SealStream(fs, key, prefix, header), CompressionLevel.Fastest);
                WriteRecord(gz, new BackupHeader { Created = TextUtil.Now() });
                foreach (var it in items)
                {
                    WriteRecord(gz, it.WithoutBlobs());
                    foreach (var blob in new[] { it.Html, it.Rtf, it.Image, it.Thumb })
                    {
                        WriteInt(gz, blob?.Length ?? -1);
                        if (blob is not null) gz.Write(blob);
                    }
                    count++;
                }
                WriteInt(gz, 0);
            }
            File.Move(temp, path, overwrite: true);
            return count;
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // Checks the password (by opening the first chunk) before any item is read.
    public static BackupReader Open(string path, string password)
    {
        var fs = File.OpenRead(path);
        try
        {
            var magic = new byte[MagicV2.Length];
            if (fs.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) < magic.Length)
                throw new InvalidDataException(Loc.T("Bu dosya bir clipdeck yedeği değil."));
            if (magic.AsSpan().SequenceEqual(MagicV1))
            {
                var legacy = ReadVersion1(fs, password);
                fs.Dispose();
                return new BackupReader(legacy);
            }
            if (!magic.AsSpan().SequenceEqual(MagicV2))
                throw new InvalidDataException(Loc.T("Bu dosya bir clipdeck yedeği değil."));

            var rest = new byte[4 + SaltSize + PrefixSize];
            fs.ReadExactly(rest);
            int iterations = BinaryPrimitives.ReadInt32LittleEndian(rest);
            if (iterations is < 10_000 or > 10_000_000) throw new InvalidDataException(Corrupt);
            var salt = rest[4..(4 + SaltSize)];
            var prefix = rest[(4 + SaltSize)..];
            var key = DeriveKey(password, salt, iterations);
            var open = new OpenStream(fs, key, prefix, Header(iterations, salt, prefix));
            CryptographicOperations.ZeroMemory(key);
            return new BackupReader(new GZipStream(open, CompressionMode.Decompress));
        }
        catch (EndOfStreamException)
        {
            fs.Dispose();
            throw new InvalidDataException(Corrupt);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    static BackupFile ReadVersion1(FileStream fs, string password)
    {
        using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        int iterations = r.ReadInt32();
        if (iterations is < 10_000 or > 10_000_000) throw new InvalidDataException(Corrupt);
        var salt = r.ReadBytes(SaltSize);
        var nonce = r.ReadBytes(NonceSize);
        var tag = r.ReadBytes(TagSize);
        if (tag.Length < TagSize) throw new InvalidDataException(Corrupt);
        var cipher = r.ReadBytes((int)(fs.Length - fs.Position));

        var key = DeriveKey(password, salt, iterations);
        var packed = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, packed, MagicV1);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException(Loc.T("Parola yanlış ya da dosya bozuk."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<BackupFile>(gz) ?? throw new InvalidDataException(Corrupt);
    }

    static byte[] Header(int iterations, byte[] salt, byte[] prefix)
    {
        var h = new byte[MagicV2.Length + 4 + SaltSize + PrefixSize];
        MagicV2.CopyTo(h, 0);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(MagicV2.Length), iterations);
        salt.CopyTo(h, MagicV2.Length + 4);
        prefix.CopyTo(h, MagicV2.Length + 4 + SaltSize);
        return h;
    }

    static void WriteRecord<T>(Stream s, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        WriteInt(s, bytes.Length);
        s.Write(bytes);
    }

    static void WriteInt(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        s.Write(b);
    }

    internal static int ReadInt(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    internal static byte[]? ReadBlock(Stream s, bool allowNull)
    {
        int n = ReadInt(s);
        if (n == -1 && allowNull) return null;
        if (n < 0 || n > MaxRecord) throw new InvalidDataException(Corrupt);
        var b = new byte[n];
        s.ReadExactly(b);
        return b;
    }

    internal static T ReadRecord<T>(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException(Corrupt);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(Corrupt);
        }
    }

    // Chunk nonce: random per-file prefix, chunk counter, and a flag on the last chunk so a cut-off file is detected.
    internal static void Nonce(byte[] prefix, uint counter, bool last, Span<byte> nonce)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[PrefixSize..], counter);
        nonce[NonceSize - 1] = last ? (byte)1 : (byte)0;
    }

    static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
}

internal sealed class BackupReader : IDisposable
{
    readonly Stream? _stream;
    readonly BackupFile? _legacy;

    public long Created { get; }

    internal BackupReader(BackupFile legacy)
    {
        _legacy = legacy;
        Created = legacy.Created;
    }

    internal BackupReader(Stream stream)
    {
        _stream = stream;
        try
        {
            var header = Backup.ReadRecord<BackupHeader>(Backup.ReadBlock(stream, allowNull: false)!);
            Created = header.Created;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(Backup.Corrupt);
        }
    }

    public IEnumerable<BackupItem> Items()
    {
        if (_legacy is not null)
        {
            foreach (var it in _legacy.Items) yield return it;
            yield break;
        }
        while (Next() is { } item) yield return item;
    }

    BackupItem? Next()
    {
        try
        {
            var json = Backup.ReadBlock(_stream!, allowNull: false)!;
            if (json.Length == 0) return null;
            var item = Backup.ReadRecord<BackupItem>(json);
            item.Html = Backup.ReadBlock(_stream!, allowNull: true);
            item.Rtf = Backup.ReadBlock(_stream!, allowNull: true);
            item.Image = Backup.ReadBlock(_stream!, allowNull: true);
            item.Thumb = Backup.ReadBlock(_stream!, allowNull: true);
            return item;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(Backup.Corrupt);
        }
    }

    public void Dispose() => _stream?.Dispose();
}

// Write side of the chunked AES-GCM stream. Only the last chunk is shorter than ChunkSize (possibly empty).
internal sealed class SealStream(Stream inner, byte[] key, byte[] prefix, byte[] aad) : Stream
{
    readonly AesGcm _aes = new(key, Backup.TagSize);
    readonly byte[] _buf = new byte[Backup.ChunkSize];
    readonly byte[] _out = new byte[Backup.ChunkSize + Backup.TagSize];
    int _len;
    uint _counter;
    bool _closed;

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, _buf.Length - _len);
            data[..n].CopyTo(_buf.AsSpan(_len));
            _len += n;
            data = data[n..];
            if (_len == _buf.Length) Seal(last: false);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    void Seal(bool last)
    {
        Span<byte> nonce = stackalloc byte[Backup.NonceSize];
        Backup.Nonce(prefix, checked(_counter++), last, nonce);
        _aes.Encrypt(nonce, _buf.AsSpan(0, _len), _out.AsSpan(0, _len), _out.AsSpan(_len, Backup.TagSize), aad);
        inner.Write(_out, 0, _len + Backup.TagSize);
        _len = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            Seal(last: true);
            inner.Flush();
            _aes.Dispose();
        }
        base.Dispose(disposing);
    }

    // A partial chunk cannot be sealed early; data goes out a chunk at a time and at Dispose.
    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_closed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

// Read side: a chunk shorter than full size is the last one; running out of data before it means the file was cut.
internal sealed class OpenStream(Stream inner, byte[] key, byte[] prefix, byte[] aad) : Stream
{
    readonly AesGcm _aes = new(key, Backup.TagSize);
    readonly byte[] _in = new byte[Backup.ChunkSize + Backup.TagSize];
    readonly byte[] _buf = new byte[Backup.ChunkSize];
    int _pos, _len;
    uint _counter;
    bool _last;

    public override int Read(Span<byte> dest)
    {
        while (_pos == _len)
        {
            if (_last || dest.Length == 0) return 0;
            Fill();
        }
        int n = Math.Min(dest.Length, _len - _pos);
        _buf.AsSpan(_pos, n).CopyTo(dest);
        _pos += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    void Fill()
    {
        int n = inner.ReadAtLeast(_in, _in.Length, throwOnEndOfStream: false);
        if (n < Backup.TagSize) throw new InvalidDataException(Backup.Corrupt);
        _last = n < _in.Length;
        int plain = n - Backup.TagSize;
        Span<byte> nonce = stackalloc byte[Backup.NonceSize];
        Backup.Nonce(prefix, checked(_counter++), _last, nonce);
        try
        {
            _aes.Decrypt(nonce, _in.AsSpan(0, plain), _in.AsSpan(plain, Backup.TagSize), _buf.AsSpan(0, plain), aad);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new CryptographicException(Loc.T("Parola yanlış ya da dosya bozuk."));
        }
        _pos = 0;
        _len = plain;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
