using System.Text.RegularExpressions;

namespace ClipDeck.Core;

// Strips tracking parameters while leaving the rest of the query untouched (order and encoding kept as typed).
internal static partial class LinkCleaner
{
    static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "gclsrc", "dclid", "gbraid", "wbraid", "msclkid", "yclid", "ttclid", "twclid",
        "igshid", "igsh", "mc_cid", "mc_eid", "_hsenc", "_hsmi", "mkt_tok", "vero_id", "oly_anon_id", "oly_enc_id",
        "rb_clickid", "s_cid", "ref_src", "ref_url", "_ga", "_gl", "li_fat_id", "srsltid", "spm", "scm",
        "trk", "trkCampaign", "sc_campaign", "sc_channel", "wickedid", "ncid", "cmpid", "zanpid",
    };

    // "si" is a share tracker on these hosts but a real parameter elsewhere.
    static readonly string[] ShareTrackerHosts = ["youtube.com", "youtu.be", "spotify.com", "music.youtube.com"];

    public static bool IsTracking(string name, string host) =>
        name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
        || Tracking.Contains(name)
        || (name.Equals("si", StringComparison.OrdinalIgnoreCase)
            && ShareTrackerHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)));

    public static string CleanUrl(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return url;
        int hash = url.IndexOf('#', q);
        string beforeQuery = url[..q];
        string query = hash < 0 ? url[(q + 1)..] : url[(q + 1)..hash];
        string fragment = hash < 0 ? "" : url[hash..];

        string host = Uri.TryCreate(url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url, UriKind.Absolute, out var u)
            ? u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host
            : "";
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTracking(Uri.UnescapeDataString(p.Split('=', 2)[0]), host))
            .ToList();
        return beforeQuery + (kept.Count > 0 ? "?" + string.Join("&", kept) : "") + fragment;
    }

    // Cleans every link inside a larger text as well, so it works on messages that contain URLs.
    public static string CleanText(string text) => UrlInText().Replace(text, m => CleanUrl(m.Value));

    public static bool CanClean(string text) => text.Contains('?') && CleanText(text) != text;

    // Auto mode only rewrites a copy that is nothing but one link; longer text is kept exactly as copied.
    public static string? CleanBareLink(string text)
    {
        var t = text.Trim();
        if (!TextUtil.ParseLink(t).Item1) return null;
        var clean = CleanUrl(t);
        return clean == t ? null : clean;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInText();
}
