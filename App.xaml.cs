using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ClipDeck.Core;
using ClipDeck.UI;

namespace ClipDeck;

public partial class App : Application
{
    // CLIPDECK_DATA points a development/test run at a separate history so it never touches the real one.
    static readonly string? DataOverride = Environment.GetEnvironmentVariable("CLIPDECK_DATA");
    public static bool IsTestData => !string.IsNullOrEmpty(DataOverride);
    public static readonly string DataDir = IsTestData
        ? DataOverride!
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipdeck");

    static string SettingsPath => Path.Combine(DataDir, "ayarlar.json");
    static string KeyPath => Path.Combine(DataDir, "anahtar.bin");
    static string DbPath => Path.Combine(DataDir, "gecmis.db");
    const string MutexName = @"Local\clipdeck.single";
    const string ShowEvent = @"Local\clipdeck.show";

    Mutex? _mutex;
    bool _ownsMutex;
    EventWaitHandle? _showSignal;
    EventWaitHandle? _quitSignal;
    ClipStore? _store;
    ClipboardMonitor? _monitor;
    WinVHook? _hook;
    PopupWindow? _popup;
    SettingsWindow? _settingsWindow;
    DispatcherTimer? _debounce;
    DispatcherTimer? _housekeeping;
    volatile bool _exiting;

    internal AppSettings Settings { get; private set; } = new();
    internal ClipManager Manager { get; private set; } = null!;
    internal TrayIcon? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Directory.CreateDirectory(DataDir);
        Log.FilePath = Path.Combine(DataDir, "clipdeck.log");
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("arayüz", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("ÖLÜMCÜL " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("görev", ex.Exception);
            ex.SetObserved();
        };

        Settings = AppSettings.Load(SettingsPath);
        Loc.Init(Settings.Language);
        Theme.Apply(Settings);
        // A small popup gains nothing from GPU rendering, while the Direct3D device costs tens of MB per process.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        bool uninstall = e.Args.Contains("--kaldir", StringComparer.OrdinalIgnoreCase);
        if (uninstall || Installer.IsSetupExe(e.Args))
        {
            if (!uninstall && e.Args.Contains("--sessiz", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Installer.InstallQuietly();
                    Log.Write("sessiz kurulum tamamlandı");
                }
                catch (Exception ex)
                {
                    Log.Error("sessiz kurulum", ex);
                }
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new SetupWindow(uninstall).Show();
            return;
        }

        if (!AcquireSingleInstance(e.Args)) return;

        if (Settings.RunAsAdmin && !Autostart.IsElevated && !e.Args.Contains("--yonetici-degil", StringComparer.OrdinalIgnoreCase)
            && RelaunchElevated())
        {
            Quit();
            return;
        }

        var crypto = OpenCrypto();
        var notice = ShowMigrationNotice();
        _store = OpenStore(crypto);
        notice?.Close();
        Manager = new ClipManager(_store, crypto, Settings);
        var loadTimer = Stopwatch.StartNew();
        Manager.Load();
        Log.Write($"geçmiş yüklendi: {loadTimer.ElapsedMilliseconds} ms");

        _popup = new PopupWindow();
        _popup.EnsureCreated();
        Manager.Changed += () => _popup.RefreshIfVisible();

        _monitor = new ClipboardMonitor();
        ClipboardIo.Owner = _monitor.Handle;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = Manager.CaptureAsync();
        };
        _monitor.Changed += () =>
        {
            _debounce.Stop();
            _debounce.Start();
        };

        _hook = new WinVHook { ClipboardEnabled = Settings.TakeOverWinV, EmojiEnabled = Settings.TakeOverWinPeriod };
        _hook.Triggered += (hwnd, kind) => Dispatcher.BeginInvoke(() => _popup.Toggle(hwnd, kind));
        _hook.Start();

        Tray = new TrayIcon(this);
        ApplyAutostart();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        Listen(_showSignal, () => ShowPopup(IntPtr.Zero));
        _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEvent);
        Listen(_quitSignal, Quit);

        // Age-based cleanup has to run even when nothing new is copied for days.
        _housekeeping = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _housekeeping.Tick += (_, _) => Manager.Trim();
        _housekeeping.Start();

        var startup = DateTime.Now - Process.GetCurrentProcess().StartTime;
        Log.Write($"başladı — {Manager.Items.Count} öğe, {startup.TotalMilliseconds:0} ms{(Autostart.IsElevated ? " (yönetici)" : "")}");

        if (!Settings.WelcomeShown)
        {
            new WelcomeWindow().Show();
        }
        else if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            Tray.Balloon(Loc.T("clipdeck çalışıyor"), Settings.TakeOverWinV ? Loc.T("Win+V ile aç.") : Loc.T("Tepsi simgesine tıklayarak aç."));
        }

        // Warm the emoji data once the app is idle so the first Emoji tab opens instantly.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            try
            {
                await PickerData.EnsureLoadedAsync();
                // Warm-up also starts the panel's idle timer, which trims memory once things are quiet.
                _popup.WarmUp();
            }
            catch (Exception ex)
            {
                Log.Error("bölüm verisi", ex);
            }
        });
    }

    bool AcquireSingleInstance(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (first)
        {
            _ownsMutex = true;
            return true;
        }
        // "--bekle": started by a restart/elevation hand-over; wait for the previous instance to exit.
        if (args.Contains("--bekle", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
            if (_ownsMutex) return true;
        }
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowEvent);
            show.Set();
        }
        catch
        {
        }
        Shutdown();
        return false;
    }

    void Listen(EventWaitHandle handle, Action action)
    {
        new Thread(() =>
        {
            while (handle.WaitOne() && !_exiting)
                Dispatcher.BeginInvoke(action);
        })
        { IsBackground = true, Name = "clipdeck-signal" }.Start();
    }

    public bool RelaunchElevated() => Relaunch(elevated: true);

    public bool Restart() => Relaunch(elevated: false);

    bool Relaunch(bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, "--tray --bekle") { UseShellExecute = true };
            if (elevated) psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex)
        {
            Log.Write("yeniden başlatılamadı: " + ex.Message);
            return false;
        }
    }

    public static void TrimMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        // A tray app sits idle most of the time; trimmed pages come back cheaply from the standby list when the panel opens.
        Native.Win32.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    public void SaveSettings() => Settings.Save(SettingsPath);

    Crypto OpenCrypto()
    {
        try
        {
            return new Crypto(KeyPath);
        }
        catch (CryptographicException ex)
        {
            Log.Error("anahtar açılamadı", ex);
            QuarantineData();
            MessageBox.Show(
                Loc.T("Şifreleme anahtarı açılamadı (farklı Windows hesabı ya da bozuk dosya).\nEski dosyalar \".bozuk-…\" uzantısıyla kenara alındı, yeni ve boş bir geçmiş başlatıldı."),
                "clipdeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new Crypto(KeyPath);
        }
    }

    // The one-off schema upgrade blocks start-up; on a large history say so instead of looking frozen.
    Window? ShowMigrationNotice()
    {
        try
        {
            if (!File.Exists(DbPath) || new FileInfo(DbPath).Length < 20 * 1024 * 1024 || !ClipStore.NeedsMigration(DbPath)) return null;
        }
        catch (Exception ex)
        {
            Log.Error("geçiş denetimi", ex);
            return null;
        }
        var notice = new Window
        {
            Title = "clipdeck",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            Background = (Brush)FindResource("Bg"),
            Content = new System.Windows.Controls.TextBlock
            {
                Text = Loc.T("clipdeck geçmişini yeni biçime taşıyor. Büyük geçmişlerde bu bir dakika sürebilir, lütfen bekle…"),
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("Text"),
                FontSize = 13,
            },
        };
        notice.Show();
        Dispatcher.Invoke(DispatcherPriority.Background, () => { });
        return notice;
    }

    ClipStore OpenStore(Crypto crypto)
    {
        try
        {
            return new ClipStore(DbPath, crypto);
        }
        catch (SqliteException ex)
        {
            Log.Error("veritabanı açılamadı", ex);
            QuarantineData(keepKey: true);
            MessageBox.Show(
                Loc.T("Geçmiş veritabanı açılamadı.\nEski dosya \".bozuk-…\" uzantısıyla kenara alındı, yeni bir geçmiş başlatıldı."),
                "clipdeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new ClipStore(DbPath, crypto);
        }
    }

    static void QuarantineData(bool keepKey = false)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var files = new List<string> { DbPath, DbPath + "-wal", DbPath + "-shm" };
        if (!keepKey) files.Add(KeyPath);
        foreach (var f in files)
            if (File.Exists(f)) File.Move(f, $"{f}.bozuk-{stamp}");
    }

    public void ShowPopup(IntPtr target) => _popup?.ShowFor(target);

    public void TogglePause()
    {
        Manager.Paused = !Manager.Paused;
        Tray?.SetPaused(Manager.Paused);
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ApplySettings()
    {
        Settings.Save(SettingsPath);
        if (_hook is not null)
        {
            _hook.ClipboardEnabled = Settings.TakeOverWinV;
            _hook.EmojiEnabled = Settings.TakeOverWinPeriod;
        }
        ApplyAutostart();
        Theme.Apply(Settings);
        if (ClipItem.MaskSensitive != (Settings.SensitiveMode == "mask")) Manager.Load();
        Manager.Trim();
    }

    // Admin mode runs schtasks.exe, which takes a moment; keep it off the UI thread.
    void ApplyAutostart()
    {
        if (IsTestData) return;
        bool enabled = Settings.StartWithWindows, admin = Settings.RunAsAdmin && Autostart.IsElevated;
        var path = Installer.LaunchPath;
        Task.Run(() => Autostart.Apply(enabled, admin, path));
    }

    public void Quit()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _hook?.Dispose();
        _monitor?.Dispose();
        Tray?.Dispose();
        _store?.Dispose();
        _showSignal?.Set();
        _quitSignal?.Set();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        Installer.RemoveFolderAfterExit();
        base.OnExit(e);
    }
}
