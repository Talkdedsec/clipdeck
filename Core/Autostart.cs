using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ClipDeck.Core;

// Normal mode uses the per-user Run key. Admin mode needs a scheduled task: a Run entry cannot start
// an elevated program without a UAC prompt at every sign-in.
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "clipdeck";
    const string TaskName = "clipdeck";

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static void Apply(bool enabled, bool admin, string? exePath = null)
    {
        exePath ??= Environment.ProcessPath!;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && !admin) key.SetValue(Name, $"\"{exePath}\" --tray");
            else if (key.GetValue(Name) is not null) key.DeleteValue(Name, false);
        }
        catch (Exception ex)
        {
            Log.Error("otomatik başlatma", ex);
        }

        if (!IsElevated) return;
        if (enabled && admin) CreateTask(exePath);
        else DeleteTask();
    }

    static void CreateTask(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{Escape(exePath)}</Command><Arguments>--tray</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "clipdeck-gorev.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static void DeleteTask() => Schtasks($"/Delete /TN \"{TaskName}\" /F", quiet: true);

    // The task cache is readable without admin rights, unlike the task itself.
    public static bool TaskExists()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\" + TaskName);
        return k is not null;
    }

    // The admin-mode task was created elevated, so removing it from a normal process needs one UAC prompt.
    public static void DeleteTaskElevated()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(15_000);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write("zamanlanmış görev silinemedi: " + ex.Message);
        }
    }

    static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    static void Schtasks(string args, bool quiet = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return;
            p.WaitForExit(10_000);
            if (p.ExitCode != 0 && !quiet) Log.Write("schtasks: " + p.StandardError.ReadToEnd().Trim());
        }
        catch (Exception ex)
        {
            Log.Error("zamanlanmış görev", ex);
        }
    }
}
