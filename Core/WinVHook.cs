using System.Runtime.InteropServices;
using static ClipDeck.Native.Win32;

namespace ClipDeck.Core;

public enum HotkeyKind { Clipboard, Emoji }

// Low-level keyboard hook on its own thread so a busy UI thread can never make Windows drop the hook.
// Swallows V / period while Win is held, so Explorer's own panels never see Win+V or Win+.
internal sealed class WinVHook : IDisposable
{
    public event Action<IntPtr, HotkeyKind>? Triggered;
    public volatile bool ClipboardEnabled = true;
    public volatile bool EmojiEnabled = true;

    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    LowLevelKeyboardProc? _proc;
    uint _swallowed;
    bool _lwin, _rwin;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = Proc;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) Log.Write("Klavye kancası kurulamadı: " + Marshal.GetLastWin32Error());
            ready.Set();
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        })
        {
            IsBackground = true,
            Name = "clipdeck-hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait(3000);
    }

    // Async key state can lag behind back-to-back events, so also track Win from what the hook itself sees.
    bool WinDown() => _lwin || _rwin || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);

    // The key that types "." differs between layouts (e.g. Turkish Q), so ask the foreground layout.
    static bool IsPeriodKey(uint vk)
    {
        if (vk == VK_OEM_PERIOD) return true;
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        short scan = VkKeyScanExW('.', layout);
        return scan != -1 && (scan >> 8) == 0 && (scan & 0xFF) == vk;
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            if (k.vkCode == VK_LWIN) _lwin = down;
            else if (k.vkCode == VK_RWIN) _rwin = down;
            else if (k.dwExtraInfo != SelfInputMarker && (_swallowed == k.vkCode || WinDown()))
            {
                if (down)
                {
                    if (_swallowed == k.vkCode)
                    {
                        if (WinDown()) return 1;
                        _swallowed = 0;
                    }
                    HotkeyKind? kind = k.vkCode == VK_V ? HotkeyKind.Clipboard
                        : IsPeriodKey(k.vkCode) ? HotkeyKind.Emoji
                        : null;
                    bool enabled = kind == HotkeyKind.Clipboard ? ClipboardEnabled : kind == HotkeyKind.Emoji && EmojiEnabled;
                    if (enabled && WinDown() && !IsKeyDown(VK_CONTROL) && !IsKeyDown(VK_MENU) && !IsKeyDown(VK_SHIFT))
                    {
                        _swallowed = k.vkCode;
                        SendKeys(KeyInput(VK_MASK, false), KeyInput(VK_MASK, true));
                        var fg = GetForegroundWindow();
                        try { Triggered?.Invoke(fg, kind!.Value); }
                        catch (Exception ex) { Log.Error("kanca", ex); }
                        return 1;
                    }
                }
                else if (_swallowed == k.vkCode)
                {
                    _swallowed = 0;
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}

internal static class PasteService
{
    public static async Task PasteAsync(IntPtr target, IntPtr focus, int caretBack = 0)
    {
        if (!await PrepareAsync(target, focus)) return;
        Send([KeyInput(VK_CONTROL, false), KeyInput(VK_V, false), KeyInput(VK_V, true), KeyInput(VK_CONTROL, true)]);
        if (caretBack <= 0) return;
        // Let the target finish pasting before stepping the caret back to the snippet's {imleç} mark.
        await Task.Delay(60);
        var left = new INPUT[Math.Min(caretBack, 5000) * 2];
        for (int i = 0; i < left.Length; i += 2)
        {
            left[i] = KeyInput(VK_LEFT, false, extended: true);
            left[i + 1] = KeyInput(VK_LEFT, true, extended: true);
        }
        Send(left);
    }

    // Types text directly as Unicode key events, leaving the user's clipboard untouched.
    public static async Task TypeAsync(IntPtr target, IntPtr focus, string text)
    {
        if (!await PrepareAsync(target, focus)) return;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = UnicodeInput(text[i], false);
            inputs[i * 2 + 1] = UnicodeInput(text[i], true);
        }
        Send(inputs);
    }

    static async Task<bool> PrepareAsync(IntPtr target, IntPtr focus)
    {
        if (!IsWindow(target)) return false;
        await Task.Delay(30);
        if (GetForegroundWindow() != target || (focus != IntPtr.Zero && FocusOf(target) != focus)) Restore(target, focus);
        // Wait for the user to let go of modifiers, otherwise the target would receive e.g. Ctrl+Shift+V.
        for (int i = 0; i < 50 && AnyModifierDown(); i++) await Task.Delay(20);
        var fg = GetForegroundWindow();
        if (fg != target) Log.Write($"hedef öne alınamadı (hedef={target}, önde={fg})");
        return true;
    }

    static void Send(INPUT[] inputs)
    {
        uint sent = SendKeys(inputs);
        if (sent != inputs.Length) Log.Write($"SendInput {sent}/{inputs.Length} (hata {Marshal.GetLastWin32Error()})");
    }

    static bool AnyModifierDown() =>
        IsKeyDown(VK_SHIFT) || IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
}
