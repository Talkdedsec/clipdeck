using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClipDeck.Core;

public sealed class AppSettings
{
    public bool TakeOverWinV { get; set; } = true;
    public bool TakeOverWinPeriod { get; set; } = true;
    public int MaxAgeDays { get; set; }
    public string SensitiveMode { get; set; } = "mask";
    public string LinkCleaning { get; set; } = "menu";
    public string Language { get; set; } = "tr";
    public bool RunAsAdmin { get; set; }
    public bool CheckUpdates { get; set; }
    public long LastUpdateCheck { get; set; }
    public string? NotifiedVersion { get; set; }
    public bool WelcomeShown { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MoveUsedToTop { get; set; } = true;
    public bool CaptureImages { get; set; } = true;
    public bool CaptureFiles { get; set; } = true;
    public bool AutoOcr { get; set; } = true;
    public bool RespectPrivacyFlags { get; set; } = true;
    public int MaxItems { get; set; }
    public string Theme { get; set; } = "system";
    public List<string> ExcludedApps { get; set; } =
        ["KeePass", "KeePassXC", "1Password", "Bitwarden", "Dashlane", "LastPass", "Enpass", "Proton Pass"];

    public string? AccentColor { get; set; }
    public string? BackgroundImage { get; set; }
    public int BackgroundBlur { get; set; } = 14;
    public int BackgroundDim { get; set; } = 45;
    public int CardOpacity { get; set; } = 100;
    public int PanelWidth { get; set; } = 400;
    public int PanelHeight { get; set; } = 540;
    public int SkinTone { get; set; }
    public Dictionary<string, List<string>> Recents { get; set; } = [];

    public const int MinWidth = 340, MaxWidth = 720, MinHeight = 420, MaxHeight = 900;

    public void Normalize()
    {
        // A hand-edited file can say null for lists; treat that as empty rather than crash later.
        ExcludedApps = ExcludedApps?.Where(a => a is not null).ToList() ?? [];
        Recents = Recents?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
        BackgroundBlur = Math.Clamp(BackgroundBlur, 0, 40);
        BackgroundDim = Math.Clamp(BackgroundDim, 0, 90);
        CardOpacity = Math.Clamp(CardOpacity, 20, 100);
        PanelWidth = Math.Clamp(PanelWidth, MinWidth, MaxWidth);
        PanelHeight = Math.Clamp(PanelHeight, MinHeight, MaxHeight);
        SkinTone = Math.Clamp(SkinTone, 0, 5);
        MaxAgeDays = Math.Clamp(MaxAgeDays, 0, 3650);
        if (SensitiveMode is not ("mask" or "skip" or "off")) SensitiveMode = "mask";
        if (LinkCleaning is not ("auto" or "menu" or "off")) LinkCleaning = "menu";
    }

    public void AddRecent(string section, string value, int max = 27)
    {
        if (!Recents.TryGetValue(section, out var list)) Recents[section] = list = [];
        list.Remove(value);
        list.Insert(0, value);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ayarlar okunamadı", ex);
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        try
        {
            // Written aside and swapped in, so a crash mid-write cannot leave a broken file that resets every setting.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("ayarlar kaydedilemedi", ex);
        }
    }

    public bool IsExcluded(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (var raw in ExcludedApps)
        {
            var name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length > 0 && string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
