using System.Reflection;
using System.Text.RegularExpressions;
using ClipDeck.Core;

namespace ClipDeck.Tests;

// Guards against UI text that would silently stay Turkish in the English interface.
public class LocalizationTests
{
    static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "clipdeck.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("clipdeck.csproj bulunamadı");
        }
    }

    static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"));

    static string Unescape(string s) => Regex.Unescape(s);

    static IEnumerable<string> UsedKeys()
    {
        foreach (var file in SourceFiles("*.cs"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Loc\\.(?:T|F)\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*[,)]"))
                yield return Unescape(m.Groups[1].Value);
        foreach (var file in SourceFiles("*.xaml"))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(xaml, @"\{ui:T\s+'((?:[^'\\]|\\.)*)'\}"))
                yield return m.Groups[1].Value.Replace("\\'", "'").Replace("&quot;", "\"");
            foreach (Match m in Regex.Matches(xaml, @"\{ui:T\s+([^'}\s][^}]*)\}"))
                yield return m.Groups[1].Value.Trim();
        }
    }

    static Dictionary<string, string> English =>
        (Dictionary<string, string>)typeof(Loc).GetField("English", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void EveryUsedKeyHasAnEnglishText()
    {
        var missing = UsedKeys().Distinct().Where(k => !English.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Eksik çeviriler:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void DataDrivenNamesHaveEnglishTexts()
    {
        var names = TextTransforms.All.Select(t => t.Name).ToList();
        foreach (var file in new[] { "kaomoji.txt", "semboller.txt" })
            names.AddRange(File.ReadAllLines(Path.Combine(Root, "Assets", file))
                .Where(l => l.StartsWith("# ")).Select(l => l[2..].Split('|')[0].Trim()));
        var missing = names.Where(n => !English.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0, "Eksik çeviriler:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void FormatPlaceholdersMatch()
    {
        var bad = English.Where(p => Regex.Matches(p.Key, @"\{\d").Count != Regex.Matches(p.Value, @"\{\d").Count)
            .Select(p => p.Key).ToList();
        Assert.True(bad.Count == 0, "Yer tutucu sayısı farklı:\n" + string.Join("\n", bad));
    }
}

public class EmojiDataTests
{
    [Fact]
    public void TsvIsWellFormed()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "clipdeck.csproj"))) root = root.Parent!;
        var lines = File.ReadAllLines(Path.Combine(root.FullName, "Assets", "emoji.tsv"));
        Assert.True(lines.Length > 1500);
        foreach (var line in lines)
        {
            var f = line.Split('\t');
            Assert.Equal(6, f.Length);
            Assert.InRange(int.Parse(f[1]), 0, 8);
            Assert.False(string.IsNullOrWhiteSpace(f[0]));
            if (f[5].Length > 0) Assert.Equal(5, f[5].Split(' ').Length);
        }
        Assert.True(lines.Count(l => l.Split('\t')[3].Length > 0) > lines.Length * 0.95, "Türkçe adların çoğu dolu olmalı");
    }
}
