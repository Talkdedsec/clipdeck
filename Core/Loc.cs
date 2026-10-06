using System.Globalization;

namespace ClipDeck.Core;

// Turkish text is the key; the English table only needs entries for what differs.
public static partial class Loc
{
    public static string Language { get; private set; } = "tr";
    public static bool IsEnglish => Language == "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("tr-TR");

    public static void Init(string language)
    {
        Language = language == "en" ? "en" : "tr";
        Culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "tr-TR");
    }

    public static string T(string turkish) =>
        IsEnglish && English.TryGetValue(turkish, out var en) ? en : turkish;

    public static string F(string turkish, params object[] args) => string.Format(Culture, T(turkish), args);
}
