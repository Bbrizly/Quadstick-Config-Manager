using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public sealed class ProfileRegistryTests
{
    const string Index = """
    {
      "schemaVersion": 1,
      "games": [],
      "devices": [],
      "profiles": [
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
      ]
    }
    """;

    [Fact]
    public void Registry_parser_accepts_the_install_fields_and_double_separator_id()
    {
        var rows = ProfileRegistryClient.Parse(Index);
        var row = Assert.Single(rows);
        Assert.Equal("example-fps--pc--quadstick-fps--beginner--sam", row.Id);
        Assert.Equal("1AbCdEfGhIjKlMnOpQrStUvWxYz012345678", row.SheetId);
        Assert.Equal("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345678/edit",
            ProfileRegistryClient.SheetEditUrl(row));
    }

    [Fact]
    public void Registry_parser_rejects_duplicate_profile_ids()
    {
        var duplicated = Index.Replace("\n      ]\n    }", ",\n" +
            Index[(Index.IndexOf("        {", StringComparison.Ordinal))..Index.LastIndexOf("\n      ]", StringComparison.Ordinal)] +
            "\n      ]\n    }");
        Assert.Throws<ProfileRegistryException>(() => ProfileRegistryClient.Parse(duplicated));
    }

    [Theory]
    [InlineData("qcm://profile/minecraft--pc--quadstick-fps--standard--sam", "minecraft--pc--quadstick-fps--standard--sam")]
    [InlineData("https://example.com/profile/nope", null)]
    [InlineData("qcm://profile/../../bad", null)]
    public void Deep_link_accepts_only_the_profile_route(string raw, string? expected)
    {
        var ok = RegistryDeepLink.TryGetProfileId(new Uri(raw), out var actual);
        Assert.Equal(expected is not null, ok);
        if (expected is not null) Assert.Equal(expected, actual);
    }
}
