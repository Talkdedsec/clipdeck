using ClipDeck.Core;

namespace ClipDeck.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.1", "0.4.1")]
    [InlineData("V1.2", "1.2.0")]
    [InlineData("v1.0.0-beta.2", "1.0.0")]
    [InlineData(" v2.10.3+build5 ", "2.10.3")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("son-surum")]
    [InlineData(null)]
    public void RejectsOtherTags(string? tag) => Assert.Null(UpdateChecker.ParseVersion(tag));

    [Theory]
    [InlineData("0.4.1", "0.4.0.0", true)]
    [InlineData("0.4.0", "0.4.0.0", false)]
    [InlineData("0.3.9", "0.4.0.0", false)]
    [InlineData("1.0.0", "0.9.9.0", true)]
    public void ComparesWithAssemblyVersion(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateChecker.IsNewer(Version.Parse(candidate), Version.Parse(current)));

    [Fact]
    public void ReadsReleaseResponse()
    {
        var info = UpdateChecker.Parse("""
            { "tag_name": "v0.5.0", "prerelease": false, "html_url": "https://github.com/Talkdedsec/clipdeck/releases/tag/v0.5.0" }
            """);
        Assert.NotNull(info);
        Assert.Equal(new Version(0, 5, 0), info.Version);
        Assert.Equal("https://github.com/Talkdedsec/clipdeck/releases/tag/v0.5.0", info.Url);
    }

    [Fact]
    public void IgnoresPrereleases() =>
        Assert.Null(UpdateChecker.Parse("""{ "tag_name": "v0.5.0", "prerelease": true }"""));

    [Fact]
    public void OnlyOpensThisRepository()
    {
        var info = UpdateChecker.Parse("""{ "tag_name": "v0.5.0", "html_url": "https://example.com/indir.exe" }""");
        Assert.Equal(UpdateChecker.ReleasesPage, info!.Url);
    }
}
