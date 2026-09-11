using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public sealed class ProfileRegistryTests
{
    const string ValidProfile = """
    {
      "schemaVersion": 1,
      "id": "minecraft--pc--quadstick-fps--standard--sam",
      "title": "Standard",
      "description": "Example",
      "gameId": "minecraft",
      "platform": "pc",
      "deviceId": "quadstick-fps",
      "semanticStatus": "unmapped",
      "mappings": [],
      "tags": ["standard"],
      "contributor": {"displayName":"Sam"},
      "source": {
        "type": "google-sheet",
        "url": "https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345678/edit#gid=0"
      },
      "snapshot": {
        "file": "profile.csv",
        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
      },
      "createdAt": "2026-09-11T00:00:00.000Z"
    }
    """;

    static string Catalog(params string[] profiles) =>
        "{\"schemaVersion\":1,\"games\":[],\"devices\":[],\"profiles\":[" +
        string.Join(',', profiles) + "]}";

    [Fact]
    public void Registry_parser_accepts_new_registry_shape_and_double_separator_id()
    {
        var rows = ProfileRegistryClient.Parse(Catalog(ValidProfile));
        var row = Assert.Single(rows);
        Assert.Equal("minecraft--pc--quadstick-fps--standard--sam", row.Id);
        Assert.Equal("minecraft", row.GameId);
        Assert.Equal("quadstick-fps", row.DeviceId);
        Assert.Equal("google-sheet", row.SourceType);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", row.SnapshotSha256);
        Assert.Equal(
            "https://raw.githubusercontent.com/Bbrizly/Adaptive-Profiles/main/data/profiles/minecraft/quadstick-fps/minecraft--pc--quadstick-fps--standard--sam/profile.csv",
            ProfileRegistryClient.CsvUrl(row));
    }

    [Fact]
    public void Registry_parser_rejects_duplicate_profile_ids() =>
        Assert.Throws<ProfileRegistryException>(() =>
            ProfileRegistryClient.Parse(Catalog(ValidProfile, ValidProfile)));

    [Fact]
    public void Registry_parser_rejects_lookalike_google_host()
    {
        var bad = ValidProfile.Replace("docs.google.com/spreadsheets", "docs.google.com.evil.example/spreadsheets");
        Assert.Throws<ProfileRegistryException>(() => ProfileRegistryClient.Parse(Catalog(bad)));
    }

    [Fact]
    public void Registry_parser_rejects_bad_snapshot_hash()
    {
        var bad = ValidProfile.Replace(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "not-a-hash");
        Assert.Throws<ProfileRegistryException>(() => ProfileRegistryClient.Parse(Catalog(bad)));
    }

    [Theory]
    [InlineData("qcm://profile/minecraft--pc--quadstick-fps--standard--sam", "minecraft--pc--quadstick-fps--standard--sam")]
    [InlineData("https://example.com/profile/nope", null)]
    [InlineData("qcm://profile/../../bad", null)]
    [InlineData("qcm://profile/%2e%2e/bad", null)]
    [InlineData("qcm://profile/good?next=bad", null)]
    [InlineData("qcm://profile/good#bad", null)]
    [InlineData("qcm://user@profile/good", null)]
    [InlineData("qcm://profile/good/extra", null)]
    public void Deep_link_accepts_only_the_canonical_profile_route(string raw, string? expected)
    {
        var ok = RegistryDeepLink.TryGetProfileId(new Uri(raw), out var actual);
        Assert.Equal(expected is not null, ok);
        if (expected is not null) Assert.Equal(expected, actual);
    }
}
