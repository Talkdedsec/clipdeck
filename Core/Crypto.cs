using System.Security.Cryptography;
using System.Text;

namespace ClipDeck.Core;

// AES-256-GCM per field; the master key lives on disk wrapped by DPAPI (current Windows user only).
internal sealed class Crypto
{
    const int NonceSize = 12, TagSize = 16;
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("clipdeck/anahtar/v1");
    readonly byte[] _enc, _mac;

    public Crypto(string keyPath)
    {
        byte[] master;
        if (File.Exists(keyPath))
        {
            master = ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser);
        }
        else
        {
            master = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, ProtectedData.Protect(master, Entropy, DataProtectionScope.CurrentUser));
        }
        if (master.Length != 32) throw new CryptographicException("Anahtar boyutu hatalı.");

        _enc = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, Encoding.UTF8.GetBytes("enc"));
        _mac = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, Encoding.UTF8.GetBytes("mac"));
        CryptographicOperations.ZeroMemory(master);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_enc, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return output;
    }

    public byte[] Decrypt(byte[] data)
    {
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Bozuk veri.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_enc, TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
        return plain;
    }

    public byte[]? EncText(string? s) => s is null ? null : Encrypt(Encoding.UTF8.GetBytes(s));
    public byte[]? EncBytes(byte[]? b) => b is null ? null : Encrypt(b);
    public string? DecText(byte[]? b) => b is null ? null : Encoding.UTF8.GetString(Decrypt(b));
    public byte[]? DecBytes(byte[]? b) => b is null ? null : Decrypt(b);

    // Keyed hash so duplicate detection never stores a guessable digest of the content.
    public string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));
    public string Hash(byte[] b) => Convert.ToHexString(HMACSHA256.HashData(_mac, b));
}
