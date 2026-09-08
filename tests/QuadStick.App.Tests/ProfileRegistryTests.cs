using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public sealed class ProfileRegistryTests
{
    const string ValidProfile = """
    {
      "schemaVersion": 1,
      "id": "example-fps--pc--quadstick-fps--beginner--sam",
      "gameId": "example-fps",
      "platform": "pc",
      "deviceId": "quadstick-fps",
      "variant": "beginner",
      "contributor": "Sam",
      "source": {
        "kind": "google-sheet",
        "sheetId": "1AbCdEfGhIjKlMnOpQrStUvWxYz012345678",
        "csvName": "Example.csv"
      },
      "bindings": [{"actionId":"attack","inputId":"right-sip"}],
      "tags": ["beginner"]
    }
    """;

    static string Catalog(params string[] profiles) =>
        "{\"schemaVersion\":1,\"games\":[],\"devices\":[],\"profiles\":[" +
        string.Join(',', profiles) + "]}";

    [Fact]
    public void Registry_parser_accepts_the_install_fields_and_double_separator_id()
    {
        var rows = ProfileRegistryClient.Parse(Catalog(ValidProfile));
        var row = Assert.Single(rows);
        Assert.Equal("example-fps--pc--quadstick-fps--beginner--sam", row.Id);
        Assert.Equal("1AbCdEfGhIjKlMnOpQrStUvWxYz012345678", row.SheetId);
        Assert.Equal("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345678/edit",
            ProfileRegistryClient.SheetEditUrl(row));
    }

    [Fact]
    public void Registry_parser_rejects_duplicate_profile_ids() =>
        Assert.Throws<ProfileRegistryException>(() =>
            ProfileRegistryClient.Parse(Catalog(ValidProfile, ValidProfile)));

    [Theory]
    [InlineData("qcm://profile/minecraft--pc--quadstick-fps--standard--sam", "minecraft--pc--quadstick-fps--standard--sam")]
    [InlineData("https://example.com/profile/nope", null)]
    [InlineData("qcm://profile/../../bad", null)]
    [InlineData("qcm://profile/%2e%2e/bad", null)]
    [InlineData("qcm://profile/good?next=bad", null)]
    [InlineData("qcm://profile/good#bad", null)]
    public void Deep_link_accepts_only_the_canonical_profile_route(string raw, string? expected)
    {
        var ok = RegistryDeepLink.TryGetProfileId(new Uri(raw), out var actual);
        Assert.Equal(expected is not null, ok);
        if (expected is not null) Assert.Equal(expected, actual);
    }
}
