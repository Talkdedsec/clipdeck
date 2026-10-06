using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ClipDeck.Core;

internal sealed record UpdateInfo(Version Version, string Url);

// Opt-in. Reads only the latest release's version number from GitHub; nothing about the user or the clipboard is sent.
internal static class UpdateChecker
{
    const string LatestApi = "https://api.github.com/repos/Talkdedsec/clipdeck/releases/latest";
    const string RepoPrefix = "https://github.com/Talkdedsec/clipdeck/";
    public const string ReleasesPage = RepoPrefix + "releases/latest";

    public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    // The newer release, or null when this copy is already the latest.
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"clipdeck/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var latest = Parse(await http.GetStringAsync(LatestApi, ct));
        return latest is not null && IsNewer(latest.Version, Current) ? latest : null;
    }

    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || ParseVersion(tag.GetString()) is not { } version) return null;
        // Only ever open this repository's own pages, whatever the response says.
        var url = root.TryGetProperty("html_url", out var u) && u.GetString() is { } s && s.StartsWith(RepoPrefix, StringComparison.Ordinal)
            ? s
            : ReleasesPage;
        return new UpdateInfo(version, url);
    }

    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int suffix = t.IndexOfAny(['-', '+']);
        if (suffix >= 0) t = t[..suffix];
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    // Assembly versions have four parts (0.4.0.0) and tags three (0.4.0); compare them as the same release.
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    internal static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);
}
