using System.Windows.Interop;
using ClipDeck.Native;

namespace ClipDeck.Core;

internal sealed class ClipboardMonitor : IDisposable
{
    readonly HwndSource _source;
    public event Action? Changed;
    public IntPtr Handle => _source.Handle;

    public ClipboardMonitor()
    {
        var p = new HwndSourceParameters("clipdeck-monitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        if (!Win32.AddClipboardFormatListener(_source.Handle))
            Log.Write("Pano dinleyicisi eklenemedi.");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_CLIPBOARDUPDATE)
        {
            Changed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Win32.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
