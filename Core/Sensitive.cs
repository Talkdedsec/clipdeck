using System.Numerics;
using System.Text.RegularExpressions;

namespace ClipDeck.Core;

public enum SensitiveKind { None, Card, Iban, Secret }

// Deliberately conservative: only checksummed numbers and machine-generated looking secrets,
// so ordinary words and code identifiers are not hidden by mistake.
internal static partial class Sensitive
{
    static readonly string[] TokenPrefixes =
    [
        "ghp_", "gho_", "ghu_", "ghs_", "github_pat_", "glpat-", "sk-", "sk_live_", "pk_live_", "rk_live_",
        "xoxb-", "xoxp-", "xoxa-", "AKIA", "ASIA", "AIza", "ya29.", "npm_", "pypi-",
    ];

    public static SensitiveKind Detect(string text)
    {
        var t = text.Trim();
        if (t.Length < 8 || t.Length > 4000) return SensitiveKind.None;
        if (t.Contains("PRIVATE KEY-----")) return SensitiveKind.Secret;

        foreach (Match m in CardPattern().Matches(t))
            if (IsCard(new string(m.Value.Where(char.IsDigit).ToArray()))) return SensitiveKind.Card;
        foreach (Match m in IbanPattern().Matches(t.ToUpperInvariant()))
        {
            var iban = m.Value.Replace(" ", "");
            if (iban.Length is >= 15 and <= 34 && IbanValid(iban)) return SensitiveKind.Iban;
        }
        if (!t.Any(char.IsWhiteSpace) && LooksSecret(t)) return SensitiveKind.Secret;
        return SensitiveKind.None;
    }

    public static string Mask(string text, SensitiveKind kind)
    {
        var t = text.Trim();
        switch (kind)
        {
            case SensitiveKind.Card:
                foreach (Match c in CardPattern().Matches(t))
                {
                    var digits = new string(c.Value.Where(char.IsDigit).ToArray());
                    if (IsCard(digits)) return $"•••• •••• •••• {digits[^4..]}";
                }
                return "•••• •••• •••• ••••";
            case SensitiveKind.Iban:
                var m = IbanPattern().Match(t.ToUpperInvariant());
                var iban = m.Success ? m.Value.Replace(" ", "") : t;
                return $"{iban[..4]} •••• •••• •••• {iban[^2..]}";
            default:
                return t.Length <= 6 ? "••••••" : $"{t[..2]}•••••••• ({t.Length})";
        }
    }

    static bool LooksSecret(string t)
    {
        if (t.Length > 300) return false;
        foreach (var p in TokenPrefixes)
            if (t.StartsWith(p, StringComparison.Ordinal) && t.Length >= p.Length + 12) return true;
        if (t.StartsWith("eyJ") && t.Count(c => c == '.') == 2 && t.Length > 40) return true;

        if (t.Length < 16 || t.Length > 128) return false;
        if (t.Contains("://") || PathLike().IsMatch(t) || EmailLike().IsMatch(t) || GuidLike().IsMatch(t)) return false;
        int classes = (t.Any(char.IsLower) ? 1 : 0) + (t.Any(char.IsUpper) ? 1 : 0)
                    + (t.Any(char.IsDigit) ? 1 : 0) + (t.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        // Words and identifiers (camelCase included) are made of syllables, so a large share of their letters are vowels;
        // generated secrets draw letters at random and switch character class constantly.
        return classes >= 3 && t.Any(char.IsDigit) && Entropy(t) >= 3.6 && ClassChangeRatio(t) >= 0.3 && VowelRatio(t) < 0.3;
    }

    static double VowelRatio(string s)
    {
        int letters = s.Count(char.IsLetter);
        return letters == 0 ? 0 : (double)s.Count(c => "aeıioöuüAEIİOÖUÜ".Contains(c)) / letters;
    }

    static int ClassOf(char c) => char.IsLower(c) ? 0 : char.IsUpper(c) ? 1 : char.IsDigit(c) ? 2 : 3;

    static double ClassChangeRatio(string s)
    {
        int changes = 0;
        for (int i = 1; i < s.Length; i++)
            if (ClassOf(s[i]) != ClassOf(s[i - 1])) changes++;
        return (double)changes / (s.Length - 1);
    }

    static double Entropy(string s)
    {
        var counts = s.GroupBy(c => c).Select(g => (double)g.Count() / s.Length);
        return -counts.Sum(p => p * Math.Log2(p));
    }

    // One long number in ten passes Luhn by chance, so also require a card network's leading digit
    // (2-6: Mastercard, Amex, Visa, Discover, UnionPay...; 9792: Troy). IDs and millisecond timestamps start with 1.
    static bool IsCard(string digits) =>
        digits.Length is >= 13 and <= 19 && (digits[0] is >= '2' and <= '6' || digits.StartsWith("9792")) && Luhn(digits);

    static bool Luhn(string digits)
    {
        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl && (d *= 2) > 9) d -= 9;
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    static bool IbanValid(string iban)
    {
        var moved = iban[4..] + iban[..4];
        var numeric = string.Concat(moved.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString()));
        return BigInteger.TryParse(numeric, out var n) && n % 97 == 1;
    }

    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b")]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\|/|~/)")]
    private static partial Regex PathLike();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailLike();

    [GeneratedRegex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$")]
    private static partial Regex GuidLike();
}
