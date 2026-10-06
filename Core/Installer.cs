using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClipDeck.Core;

// Per-user install: no admin rights, files under %LOCALAPPDATA%\Programs, uninstall entry under HKCU.
internal static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\clipdeck";
    public const string QuitEvent = @"Local\clipdeck.quit";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "clipdeck");
    public static string InstalledExe => Path.Combine(InstallDir, "clipdeck.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "clipdeck.lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "clipdeck.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    // Autostart should always point at the installed copy, not at whichever exe happens to be running.
    public static string LaunchPath => IsInstalled ? InstalledExe : Environment.ProcessPath!;

    public static bool IsSetupExe(string[] args) =>
        args.Contains("--kur", StringComparer.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("kurulum", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static void StopRunning()
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(QuitEvent);
            quit.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        int me = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && Others(me).Count > 0) Thread.Sleep(200);
        foreach (var p in Others(me))
        {
            try
            {
                p.Kill();
                p.WaitForExit(2000);
            }
            catch
            {
            }
        }
    }

    static List<Process> Others(int me) => Process.GetProcessesByName("clipdeck").Where(p => p.Id != me).ToList();

    public static void Install(bool startMenu, bool desktop, bool autostart, bool launch)
    {
        StopRunning();
        Directory.CreateDirectory(InstallDir);
        var source = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(source, InstalledExe);

        SetShortcut(StartMenuLink, startMenu);
        SetShortcut(DesktopLink, desktop);
        RegisterUninstall();

        Directory.CreateDirectory(App.DataDir);
        var settingsPath = Path.Combine(App.DataDir, "ayarlar.json");
        var s = AppSettings.Load(settingsPath);
        s.StartWithWindows = autostart;
        s.WelcomeShown = true;
        s.Save(settingsPath);
        Autostart.Apply(autostart, s.RunAsAdmin && Autostart.IsElevated, InstalledExe);

        if (launch) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
    }

    // "--sessiz": install or update without the window, keeping the shortcut and autostart choices already made.
    public static void InstallQuietly()
    {
        bool fresh = !IsInstalled;
        var s = AppSettings.Load(Path.Combine(App.DataDir, "ayarlar.json"));
        Install(startMenu: fresh || File.Exists(StartMenuLink), desktop: File.Exists(DesktopLink),
            autostart: fresh || s.StartWithWindows, launch: true);
    }

    public static void Uninstall(bool removeData)
    {
        StopRunning();
        Autostart.Apply(false, false, InstalledExe);
        if (!Autostart.IsElevated && Autostart.TaskExists()) Autostart.DeleteTaskElevated();
        else Autostart.DeleteTask();
        SetShortcut(StartMenuLink, false);
        SetShortcut(DesktopLink, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (removeData && Directory.Exists(App.DataDir))
        {
            try { Directory.Delete(App.DataDir, true); }
            catch (Exception ex) { Log.Error("veri silinemedi", ex); }
        }
        PendingFolderRemoval = true;
    }

    public static bool PendingFolderRemoval { get; private set; }

    // The installed exe may be the one running, so the folder can only go once this process has exited.
    public static void RemoveFolderAfterExit()
    {
        if (!PendingFolderRemoval || !Directory.Exists(InstallDir)) return;
        int pid = Environment.ProcessId;
        var script = $"/c for /l %i in (1,1,30) do (tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && ping 127.0.0.1 -n 2 >nul) & rmdir /s /q \"{InstallDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false });
    }

    static void CopyWithRetry(string source, string dest)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return;
            }
            catch (IOException) when (i < 15)
            {
                Thread.Sleep(300);
            }
        }
    }

    static void SetShortcut(string path, bool present)
    {
        if (!present)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell yok");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = Loc.T("clipdeck — pano geçmişi, emoji ve semboller");
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    static void RegisterUninstall()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        k.SetValue("DisplayName", "clipdeck");
        k.SetValue("DisplayVersion", version);
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --kaldir");
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }
}
