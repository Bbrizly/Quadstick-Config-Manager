using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// A host can keep the network off and let Google Drive through alone. That
// allowance is narrow on purpose: analytics, crash reports, the community list
// and the update check all still answer to the whole-network switch, and the
// Drive engine itself can only reach Google's API hosts.
public class DriveOnlyTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<string> Hosts = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    static MainWindow DriveOnly()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        NetworkFeature.Enabled = false;
        NetworkFeature.GoogleDriveOnly = true;
        var w = new MainWindow();
        w.Show();
        return w;
    }

    static void Reset()
    {
        NetworkFeature.Enabled = true;
        NetworkFeature.GoogleDriveOnly = false;
    }

    [Theory]
    [InlineData("https://www.googleapis.com/drive/v3/files", true)]
    [InlineData("https://sheets.googleapis.com/v4/spreadsheets/abc", true)]
    [InlineData("https://oauth2.googleapis.com/token", true)]
    [InlineData("http://www.googleapis.com/drive/v3/files", false)]   // not https
    [InlineData("https://us.i.posthog.com/batch", false)]              // analytics
    [InlineData("https://www.quadstick.com/profiles.json", false)]     // community list
    [InlineData("https://api.github.com/repos/x/y/releases/latest", false)] // update check
    [InlineData("https://docs.google.com/spreadsheets/d/x/export", false)]
    [InlineData("https://www.googleapis.com.evil.example/drive", false)]
    public async Task Only_google_api_hosts_get_through(string url, bool allowed)
    {
        var inner = new Recorder();
        using var http = new HttpClient(new GoogleOnlyHandler(inner));
        if (allowed)
        {
            (await http.GetAsync(url)).Dispose();
            Assert.Single(inner.Hosts);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(url));
            Assert.Empty(inner.Hosts);
        }
    }

    [Fact]
    public void Drive_alone_is_not_the_network()
    {
        try
        {
            NetworkFeature.Enabled = false;
            NetworkFeature.GoogleDriveOnly = false;
            Assert.False(NetworkFeature.DriveAllowed);
            NetworkFeature.GoogleDriveOnly = true;
            Assert.True(NetworkFeature.DriveAllowed);
            Assert.False(NetworkFeature.Enabled);
        }
        finally { Reset(); }
    }

    // The load-bearing one: telemetry asks the whole-network switch, never the
    // Drive allowance, so turning sharing on in a host cannot turn analytics on.
    [Fact]
    public void Telemetry_stays_off_when_only_drive_is_allowed()
    {
        try
        {
            NetworkFeature.Enabled = false;
            NetworkFeature.GoogleDriveOnly = true;
            Assert.False(Telemetry.HostAllowsSending);
            Telemetry.ApplyConsent(int.MaxValue, usage: true);
            Assert.False(Telemetry.IsLive);
            Assert.False(Telemetry.Track(TelemetryEvent.ProfileSaved));
        }
        finally { Telemetry.ApplyConsent(0, usage: false); Reset(); }
    }

    [AvaloniaFact]
    public void Everything_else_stays_off_when_only_drive_is_allowed()
    {
        try
        {
            var w = DriveOnly();
            Assert.Throws<InvalidOperationException>(() => w.HttpClient);
            w.ShowCommunityPage();
            Assert.False(w.GetVisualDescendants().OfType<DockPanel>()
                .First(p => p.Name == "CommunityPage").IsVisible);
            foreach (var name in new[] { "HomeCommunityButton", "ShellCommunityButton", "ShareButton", "HomeDriveButton" })
                Assert.False(w.GetVisualDescendants().OfType<Button>().First(b => b.Name == name).IsVisible);
            w.Close();
        }
        finally { Reset(); }
    }

    // With nothing allowed, the host's share door says so and does nothing.
    [AvaloniaFact]
    public async Task A_host_share_with_drive_off_shares_nothing()
    {
        try
        {
            NetworkFeature.Enabled = false;
            NetworkFeature.GoogleDriveOnly = false;
            var w = new MainWindow();
            w.Show();
            var result = await w.ShareForHostAsync(Path.Combine(Path.GetTempPath(), "nothing.csv"));
            Assert.Null(result.Url);
            Assert.False(string.IsNullOrEmpty(result.Message));
            Assert.False(w.DriveConnected);
            w.Close();
        }
        finally { Reset(); }
    }
}
