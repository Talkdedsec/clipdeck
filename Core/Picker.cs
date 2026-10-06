using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace ClipDeck.Core;

public enum IconKind { Emoji, Glyph, Text }

public sealed class PickItem : INotifyPropertyChanged
{
    public PickItem(string text, string name, string search, PickSection section, int category, string[]? tones)
    {
        BaseText = Text = text;
        Name = name;
        SearchIndex = TextUtil.Normalize(search);
        Section = section;
        Category = category;
        Tones = tones;
    }

    public string BaseText { get; }
    public string Text { get; private set; }
    public string Name { get; }
    internal string SearchIndex { get; }
    public PickSection Section { get; }
    public int Category { get; }
    public string[]? Tones { get; }
    public bool IsEmoji => Section.IsEmoji;
    public double CellWidth => Section.CellWidth;
    public double CellHeight => Section.CellHeight;
    public double FontSize => Section.FontSize;

    ImageSource? _image;
    bool _rendered;
    public ImageSource? Image
    {
        get
        {
            if (IsEmoji && !_rendered)
            {
                _rendered = true;
                _image = EmojiRenderer.Shared?.Render(Text, PickerData.EmojiPixels);
            }
            return _image;
        }
    }

    public bool ShowImage => IsEmoji && Image is not null;
    public bool ShowText => !ShowImage;

    bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Notify(); } }
    }

    // Recent items are separate instances so selecting one does not also highlight its twin in the category below.
    public PickItem CloneForRecent() => new(BaseText, Name, "", Section, Category, Tones) { Text = Text };

    public void ApplyTone(int tone)
    {
        var t = tone > 0 && Tones is { Length: 5 } ? Tones[tone - 1] : BaseText;
        if (t == Text) return;
        Text = t;
        ResetImage();
        Notify(nameof(Text));
    }

    public void ResetImage()
    {
        if (!_rendered) return;
        _rendered = false;
        _image = null;
        Notify(nameof(Image));
        Notify(nameof(ShowImage));
        Notify(nameof(ShowText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PickCategory : INotifyPropertyChanged
{
    public PickCategory(string name, string icon, IconKind kind)
    {
        Name = name;
        Icon = icon;
        Kind = kind;
    }

    public string Name { get; }
    public string Icon { get; }
    public IconKind Kind { get; }
    public List<PickItem> Items { get; } = [];
    public int Index { get; set; }

    public ImageSource? IconImage => Kind == IconKind.Emoji ? EmojiRenderer.Shared?.Render(Icon, PickerData.IconPixels) : null;
    public bool ShowIconImage => IconImage is not null;
    public bool ShowGlyph => Kind == IconKind.Glyph;
    public bool ShowIconText => Kind == IconKind.Text || (Kind == IconKind.Emoji && IconImage is null);

    bool _current;
    public bool IsCurrent
    {
        get => _current;
        set { if (_current != value) { _current = value; Notify(); } }
    }

    public void RefreshIcon()
    {
        Notify(nameof(IconImage));
        Notify(nameof(ShowIconImage));
        Notify(nameof(ShowIconText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PickSection
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string SearchHint { get; init; }
    public bool IsEmoji { get; init; }
    public double CellWidth { get; set; } = 40;
    public double CellHeight { get; init; } = 40;
    public double FontSize { get; init; } = 18;
    public int FixedColumns { get; init; }
    public List<PickCategory> Categories { get; } = [];
    public IEnumerable<PickItem> AllItems => Categories.SelectMany(c => c.Items);
}

public abstract class PickRow
{
    public required PickCategory Category { get; init; }
}

public sealed class PickHeaderRow : PickRow
{
    public string Title => Category.Name;
}

public sealed class PickLineRow : PickRow
{
    public required PickItem[] Items { get; init; }
}

internal static class PickerData
{
    public static string RecentName => Loc.T("Son kullanılanlar");
    static readonly string[] EmojiGroups =
    [
        "Yüzler ve duygular", "İnsanlar ve vücut", "Hayvanlar ve doğa", "Yiyecek ve içecek",
        "Seyahat ve yerler", "Etkinlikler", "Nesneler", "Semboller", "Bayraklar",
    ];
    static readonly string[] EmojiGroupIcons = ["😀", "👋", "🐶", "🍔", "🚗", "⚽", "💡", "❤️", "🏁"];

    public static int EmojiPixels { get; set; } = 32;
    public static int IconPixels { get; set; } = 25;

    public static PickSection? Emoji { get; private set; }
    public static PickSection? Kaomoji { get; private set; }
    public static PickSection? Symbols { get; private set; }

    static readonly object Gate = new();
    static Task? _loading;

    public static Task EnsureLoadedAsync()
    {
        lock (Gate)
            return _loading ??= LoadAsync();
    }

    public static bool IsLoaded => _loading is { IsCompletedSuccessfully: true };

    static async Task LoadAsync()
    {
        // Created here on the UI thread; the support check below only uses its thread-safe DirectWrite factory.
        var renderer = EmojiRenderer.Shared;
        var emoji = LoadEmoji();
        var unsupported = renderer is null
            ? []
            : await Task.Run(() => renderer.FindUnsupported(emoji.AllItems.Select(i => i.BaseText)));
        foreach (var cat in emoji.Categories)
            cat.Items.RemoveAll(i => unsupported.Contains(i.BaseText));
        emoji.Categories.RemoveAll(c => c.Items.Count == 0);
        for (int i = 0; i < emoji.Categories.Count; i++) emoji.Categories[i].Index = i;

        Emoji = emoji;
        Kaomoji = LoadText("kaomoji", Loc.T("Kaomoji"), Loc.T("Kaomoji ara"), "Assets/kaomoji.txt", perLine: true);
        Symbols = LoadText("symbols", Loc.T("Semboller"), Loc.T("Sembol ara (Türkçe kategori ya da İngilizce ad)"), "Assets/semboller.txt", perLine: false);
        Log.Write($"emoji: {emoji.AllItems.Count()} gösteriliyor, {unsupported.Count} desteklenmiyor");
    }

    static IEnumerable<string> ReadResource(string path)
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/" + path))
            ?? throw new FileNotFoundException(path);
        using var reader = new StreamReader(res.Stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line) yield return line;
    }

    static PickSection LoadEmoji()
    {
        var s = new PickSection { Key = "emoji", Title = Loc.T("Emoji"), SearchHint = Loc.T("Emoji ara (Türkçe ya da İngilizce)"), IsEmoji = true };
        for (int i = 0; i < EmojiGroups.Length; i++)
            s.Categories.Add(new PickCategory(Loc.T(EmojiGroups[i]), EmojiGroupIcons[i], IconKind.Emoji) { Index = i });

        foreach (var line in ReadResource("Assets/emoji.tsv"))
        {
            var f = line.Split('\t');
            if (f.Length < 6 || !int.TryParse(f[1], out int g) || g < 0 || g >= EmojiGroups.Length) continue;
            var tones = f[5].Length > 0 ? f[5].Split(' ') : null;
            string name = Loc.IsEnglish || f[3].Length == 0 ? f[2] : f[3];
            var search = $"{f[3]} {f[4].Replace('|', ' ')} {f[2]}";
            s.Categories[g].Items.Add(new PickItem(f[0], name, search, s, g, tones is { Length: 5 } ? tones : null));
        }
        return s;
    }

    static PickSection LoadText(string key, string title, string hint, string path, bool perLine)
    {
        var s = new PickSection
        {
            Key = key,
            Title = title,
            SearchHint = hint,
            FixedColumns = perLine ? 3 : 0,
            CellHeight = perLine ? 38 : 40,
            FontSize = perLine ? 13 : 18,
        };
        PickCategory? cat = null;
        string original = "";
        foreach (var raw in ReadResource(path))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("# "))
            {
                var parts = line[2..].Split('|', 2);
                original = parts[0].Trim();
                cat = new PickCategory(Loc.T(original), parts.Length > 1 ? parts[1].Trim() : parts[0][..1], IconKind.Text)
                {
                    Index = s.Categories.Count,
                };
                s.Categories.Add(cat);
                continue;
            }
            if (cat is null) continue;
            string[] values = perLine ? [line.Trim()] : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var v in values)
            {
                string name = perLine ? cat.Name : UnicodeName(v) ?? cat.Name;
                cat.Items.Add(new PickItem(v, name, $"{original} {cat.Name} {name}", s, cat.Index, null));
            }
        }
        return s;
    }

    [DllImport("icu.dll", EntryPoint = "u_charName", CallingConvention = CallingConvention.Cdecl)]
    static extern int IcuCharName(int code, int nameChoice, byte[] buffer, int bufferLength, ref int errorCode);

    static bool _icuMissing;

    static string? UnicodeName(string s)
    {
        if (_icuMissing || s.Length == 0) return null;
        try
        {
            int cp = char.ConvertToUtf32(s, 0);
            var buf = new byte[128];
            int err = 0;
            int len = IcuCharName(cp, 0, buf, buf.Length, ref err);
            return err > 0 || len <= 0 ? null : Encoding.ASCII.GetString(buf, 0, len).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _icuMissing = true;
            return null;
        }
    }

    public static PickCategory? RecentCategory(PickSection s, IReadOnlyList<string>? recents)
    {
        if (recents is null || recents.Count == 0) return null;
        var byText = new Dictionary<string, PickItem>();
        foreach (var it in s.AllItems) byText.TryAdd(it.BaseText, it);
        var cat = new PickCategory(RecentName, "", IconKind.Glyph) { Index = -1 };
        foreach (var r in recents)
            if (byText.TryGetValue(r, out var it)) cat.Items.Add(it.CloneForRecent());
        return cat.Items.Count == 0 ? null : cat;
    }

    public static void ResetEmojiImages()
    {
        if (Emoji is null) return;
        foreach (var it in Emoji.AllItems) it.ResetImage();
        foreach (var c in Emoji.Categories) c.RefreshIcon();
    }
}
