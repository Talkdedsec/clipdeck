using System.Text.RegularExpressions;

namespace ClipDeck.Core;

// Turkish and English names are both accepted so snippets keep working when the UI language changes.
internal static partial class SnippetVars
{
    public static string Help => Loc.IsEnglish
        ? "{date} {time} {datetime} {day} {month} {year} {clipboard} {guid} {cursor}"
        : "{tarih} {saat} {tarihsaat} {gün} {ay} {yıl} {pano} {guid} {imleç}";

    public static (string Text, int CaretBack) Expand(string text, Func<string?> clipboard)
    {
        if (!text.Contains('{')) return (text, 0);
        var now = DateTime.Now;
        string? clip = null;
        var result = Variable().Replace(text, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "tarih" or "date" => now.ToString("dd.MM.yyyy"),
            "saat" or "time" => now.ToString("HH:mm"),
            "tarihsaat" or "datetime" => now.ToString("dd.MM.yyyy HH:mm"),
            "gün" or "gun" or "day" => now.ToString("dddd", Loc.Culture),
            "ay" or "month" => now.ToString("MMMM", Loc.Culture),
            "yıl" or "yil" or "year" => now.Year.ToString(),
            "pano" or "clipboard" => clip ??= clipboard() ?? "",
            "guid" => Guid.NewGuid().ToString(),
            _ => m.Value,
        });

        var cursor = Cursor().Match(result);
        if (!cursor.Success) return (result, 0);
        result = result.Remove(cursor.Index, cursor.Length);
        int after = result.Length - cursor.Index;
        // One Left keypress steps over a whole CR+LF pair.
        int crlf = Regex.Count(result[cursor.Index..], "\r\n");
        return (result, after - crlf);
    }

    [GeneratedRegex(@"\{(tarih|saat|tarihsaat|gün|gun|ay|yıl|yil|pano|guid|date|time|datetime|day|month|year|clipboard)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Variable();

    [GeneratedRegex(@"\{(?:imleç|imlec|cursor)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Cursor();
}
