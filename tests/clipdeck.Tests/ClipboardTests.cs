using ClipDeck.Core;

namespace ClipDeck.Tests;

// Uses the real Windows clipboard (and overwrites it), so it only runs when asked: set CLIPDECK_PANO_TESTI=1.
// Close a running clipdeck first, or it records the test's file list in its history.
public class ClipboardTests
{
    static bool Enabled => Environment.GetEnvironmentVariable("CLIPDECK_PANO_TESTI") == "1";

    [Fact]
    public void ReadsFileListsIncludingLongPaths()
    {
        if (!Enabled) return;
        string[] files =
        [
            @"C:\Windows\win.ini",
            @"C:\Belgeler\Bütçe 2026.xlsx",
            @"C:\" + new string('a', 120) + @"\" + new string('b', 150) + ".txt",
        ];
        // Written by WPF, not by clipdeck: clipdeck ignores clipboard contents it put there itself.
        LoadSeed.OnSta(() =>
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(files);
            System.Windows.Clipboard.SetFileDropList(list);
            return true;
        });

        var raw = ClipboardIo.Read(respectPrivacy: false, images: false, files: true);
        Assert.NotNull(raw);
        Assert.Equal(files, raw.Files);
    }

    [Fact]
    public void IgnoresItsOwnWrites()
    {
        if (!Enabled) return;
        Assert.True(ClipboardIo.Write("clipdeck kendi yazdığını kaydetmez", null, null, null, null));
        Assert.Null(ClipboardIo.Read(respectPrivacy: true, images: true, files: true));
        Assert.Equal("clipdeck kendi yazdığını kaydetmez", ClipboardIo.ReadCurrentText());
    }
}
