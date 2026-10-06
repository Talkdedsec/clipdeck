using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static ClipDeck.Native.Win32;

namespace ClipDeck.Core;

internal sealed class RawClip
{
    public string? Text;
    public byte[]? Html, Rtf, Png, Dib;
    public string[]? Files;
    public string? ProcessName, AppName;
}

// Raw Win32 clipboard access: one short OpenClipboard per read so other apps are never blocked for long.
internal static class ClipboardIo
{
    public static IntPtr Owner;

    static readonly uint FmtHtml = RegisterClipboardFormatW("HTML Format");
    static readonly uint FmtRtf = RegisterClipboardFormatW("Rich Text Format");
    static readonly uint FmtPng = RegisterClipboardFormatW("PNG");
    static readonly uint FmtDropEffect = RegisterClipboardFormatW("Preferred DropEffect");
    static readonly uint FmtSelf = RegisterClipboardFormatW("clipdeck.self");
    // Password managers and other privacy-aware apps mark sensitive copies with these formats.
    static readonly uint FmtExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint FmtViewerIgnore = RegisterClipboardFormatW("Clipboard Viewer Ignore");
    static readonly uint FmtCanInclude = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    const int MaxBytes = 64 * 1024 * 1024;
    const int MaxRichBytes = 8 * 1024 * 1024;

    static readonly ConcurrentDictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);

    public static RawClip? Read(bool respectPrivacy, bool images, bool files)
    {
        if (IsClipboardFormatAvailable(FmtSelf)) return null;
        if (respectPrivacy && (IsClipboardFormatAvailable(FmtExclude) || IsClipboardFormatAvailable(FmtViewerIgnore))) return null;
        if (!TryOpen(IntPtr.Zero)) return null;

        var raw = new RawClip();
        IntPtr owner = IntPtr.Zero;
        try
        {
            if (respectPrivacy && IsClipboardFormatAvailable(FmtCanInclude))
            {
                var flag = ReadBytes(FmtCanInclude, 64);
                if (flag is { Length: >= 4 } && BitConverter.ToInt32(flag, 0) == 0) return null;
            }

            owner = GetClipboardOwner();

            if (files && IsClipboardFormatAvailable(CF_HDROP)) raw.Files = ReadDrop();

            if (raw.Files is null && IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    raw.Text = text;
                    raw.Html = ReadRich(FmtHtml);
                    raw.Rtf = ReadRich(FmtRtf);
                }
            }

            if (raw.Files is null && raw.Text is null && images)
            {
                if (IsClipboardFormatAvailable(FmtPng)) raw.Png = ReadBytes(FmtPng, MaxBytes);
                if (raw.Png is null && IsClipboardFormatAvailable(CF_DIBV5)) raw.Dib = ReadBytes(CF_DIBV5, MaxBytes);
                if (raw.Png is null && raw.Dib is null && IsClipboardFormatAvailable(CF_DIB)) raw.Dib = ReadBytes(CF_DIB, MaxBytes);
            }
        }
        finally
        {
            CloseClipboard();
        }

        if (raw.Files is null && raw.Text is null && raw.Png is null && raw.Dib is null) return null;
        (raw.ProcessName, raw.AppName) = ProcessInfo(owner);
        return raw;
    }

    public static string? ReadCurrentText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !TryOpen(IntPtr.Zero)) return null;
        try { return ReadText(); }
        finally { CloseClipboard(); }
    }

    public static bool Write(string? text, byte[]? html, byte[]? rtf, byte[]? png, string[]? files)
    {
        var dib = png is null ? null : ImageUtil.PngToDib(png);
        if (!TryOpen(Owner))
        {
            Log.Write($"Pano açılamadı (yazma), hata={Marshal.GetLastWin32Error()}");
            return false;
        }
        try
        {
            if (!EmptyClipboard())
            {
                Log.Write($"EmptyClipboard başarısız, hata={Marshal.GetLastWin32Error()}");
                return false;
            }
            bool any = false;
            if (files is { Length: > 0 })
            {
                any |= SetBytes(CF_HDROP, BuildDropFiles(files));
                SetBytes(FmtDropEffect, BitConverter.GetBytes(1));
            }
            if (png is not null)
            {
                any |= SetBytes(FmtPng, png);
                if (dib is not null) any |= SetBytes(CF_DIB, dib);
            }
            if (text is not null)
            {
                any |= SetBytes(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
                if (html is not null) SetBytes(FmtHtml, WithZero(html));
                if (rtf is not null) SetBytes(FmtRtf, WithZero(rtf));
            }
            SetBytes(FmtSelf, [1]);
            return any;
        }
        finally
        {
            CloseClipboard();
        }
    }

    static bool TryOpen(IntPtr owner)
    {
        for (int i = 0; i < 12; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    static byte[]? ReadBytes(uint format, int max)
    {
        var h = GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        long size = (long)(ulong)GlobalSize(h);
        if (size <= 0 || size > max) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var b = new byte[size];
            Marshal.Copy(p, b, 0, (int)size);
            return b;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    static byte[]? ReadRich(uint format) =>
        IsClipboardFormatAvailable(format) ? TrimZeros(ReadBytes(format, MaxRichBytes)) : null;

    static string? ReadText()
    {
        var b = ReadBytes(CF_UNICODETEXT, MaxBytes);
        if (b is null) return null;
        var s = Encoding.Unicode.GetString(b, 0, b.Length & ~1);
        int z = s.IndexOf('\0');
        return z >= 0 ? s[..z] : s;
    }

    static string[]? ReadDrop()
    {
        var h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return null;
        uint n = DragQueryFileW(h, 0xFFFFFFFF, null, 0);
        if (n == 0) return null;
        var list = new string[n];
        for (uint i = 0; i < n; i++)
        {
            uint len = DragQueryFileW(h, i, null, 0);
            var sb = new StringBuilder((int)len + 1);
            DragQueryFileW(h, i, sb, len + 1);
            list[i] = sb.ToString();
        }
        return list;
    }

    static bool SetBytes(uint format, byte[] data)
    {
        if (data.Length == 0) return false;
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            GlobalFree(h);
            return false;
        }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) != IntPtr.Zero) return true;
        Log.Write($"SetClipboardData başarısız: biçim={format}, boyut={data.Length}, hata={Marshal.GetLastWin32Error()}, sahip={Owner}");
        GlobalFree(h);
        return false;
    }

    static byte[] BuildDropFiles(string[] files)
    {
        var chars = Encoding.Unicode.GetBytes(string.Join("\0", files) + "\0\0");
        var b = new byte[20 + chars.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0), 20);
        BitConverter.TryWriteBytes(b.AsSpan(16), 1);
        chars.CopyTo(b, 20);
        return b;
    }

    static byte[]? TrimZeros(byte[]? b)
    {
        if (b is null) return null;
        int n = b.Length;
        while (n > 0 && b[n - 1] == 0) n--;
        return n == 0 ? null : n == b.Length ? b : b[..n];
    }

    static byte[] WithZero(byte[] b) => b.Length > 0 && b[^1] == 0 ? b : [.. b, 0];

    static (string? process, string? app) ProcessInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, null);
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return (null, null);
        var path = ProcessPath(pid);
        if (path is null) return (null, null);
        var process = Path.GetFileNameWithoutExtension(path);
        var app = AppNames.GetOrAdd(path, p =>
        {
            try
            {
                var d = FileVersionInfo.GetVersionInfo(p).FileDescription;
                return string.IsNullOrWhiteSpace(d) ? process : d.Trim();
            }
            catch
            {
                return process;
            }
        });
        return (process, app);
    }
}
