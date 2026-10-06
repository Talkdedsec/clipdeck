using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClipDeck.Core;

internal static class TextTransforms
{
    static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly (string Name, Func<string, string> Apply)[] All =
    [
        ("BÜYÜK HARF", s => s.ToUpper(Tr)),
        ("küçük harf", s => s.ToLower(Tr)),
        ("Başlık Düzeni", s => Tr.TextInfo.ToTitleCase(s.ToLower(Tr))),
        ("Boşlukları temizle", CleanSpaces),
        ("Tek satıra indir", s => Regex.Replace(s.Trim(), @"\s*\r?\n\s*", " ")),
        ("Satırları sırala", s => JoinLines(SplitLines(s).Order(StringComparer.Create(Tr, true)))),
        ("Tekrarlayan satırları sil", s => JoinLines(SplitLines(s).Distinct())),
        ("JSON güzelleştir", s => Json(s, true)),
        ("JSON sıkıştır", s => Json(s, false)),
        ("URL kodla", s => Uri.EscapeDataString(s)),
        ("URL çöz", s => Uri.UnescapeDataString(s)),
        ("Base64 kodla", s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        ("Base64 çöz", s => StrictUtf8.GetString(Convert.FromBase64String(s.Trim()))),
    ];

    public static string Explain(Exception ex) => ex switch
    {
        JsonException => Loc.T("Geçerli bir JSON değil."),
        FormatException => Loc.T("Geçerli bir Base64 değil."),
        DecoderFallbackException => Loc.T("Çözülen veri metin değil."),
        _ => Loc.T("Dönüştürülemedi: ") + ex.Message,
    };

    static IEnumerable<string> SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n');

    static string JoinLines(IEnumerable<string> lines) => string.Join("\r\n", lines);

    static string CleanSpaces(string s) =>
        JoinLines(SplitLines(s).Select(l => Regex.Replace(l, @"[ \t ]+", " ").Trim())).Trim('\r', '\n');

    static string Json(string s, bool pretty)
    {
        var node = JsonNode.Parse(s, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return node?.ToJsonString(pretty ? Pretty : Compact) ?? "null";
    }
}
