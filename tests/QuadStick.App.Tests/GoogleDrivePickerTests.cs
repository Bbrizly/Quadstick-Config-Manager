using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class GoogleDrivePickerTests
{
    [Fact]
    public void Picker_url_keeps_narrow_drive_file_scope_and_required_flags()
    {
        var uri = GoogleDrivePicker.BuildAuthorizationUri(
            "challenge", "state-token", "http://127.0.0.1:4321/", allowMultiple: true);
        var query = Uri.UnescapeDataString(uri.Query);

        Assert.Contains("scope=https://www.googleapis.com/auth/drive.file", query);
        Assert.DoesNotContain("auth/drive ", query);
        Assert.DoesNotContain("auth/drive.readonly", query);
        Assert.Contains("prompt=consent", query);
        Assert.Contains("trigger_onepick=true", query);
        Assert.Contains("allow_multiple=true", query);
        Assert.Contains("application/vnd.google-apps.spreadsheet,text/csv", query);
        Assert.Contains("code_challenge_method=S256", query);
        Assert.Contains("state=state-token", query);
    }

    [Theory]
    [InlineData("1AbCdEfGh_123-xyz", true)]
    [InlineData("short", false)]
    [InlineData("contains/slash", false)]
    [InlineData("contains space", false)]
    public void Picker_ids_are_rejected_before_they_become_paths_or_keys(string id, bool expected) =>
        Assert.Equal(expected, GoogleDrivePicker.IsPlausibleDriveId(id));
}
