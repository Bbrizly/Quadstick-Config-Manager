using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

// A host can turn the network off, and the sentence it sells that on is "this
// build makes no network calls at all". A sentence like that has to be a thing
// the suite proves, not a thing a README says: everything that could reach out
// is either gone from the window or refuses to be built.
public class OfflineTests
{
    static MainWindow Offline(Action<MainWindow>? before = null)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        NetworkFeature.Enabled = false;
        var w = new MainWindow();
        before?.Invoke(w);
        w.Show();
        return w;
    }

    static Button? Named(MainWindow w, string name) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name);

    [AvaloniaFact]
    public void Nothing_that_reaches_the_network_is_on_the_window()
    {
        try
        {
            var w = Offline();
            Assert.False(Named(w, "HomeCommunityButton")!.IsVisible);
            Assert.False(Named(w, "ShellCommunityButton")!.IsVisible);
            Assert.False(Named(w, "HomeDriveButton")!.IsVisible);
            Assert.False(Named(w, "ShareButton")!.IsVisible);
            Assert.False(w.GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "HomeSheetsPanel").IsVisible);
            w.Close();
        }
        finally { NetworkFeature.Enabled = true; }
    }

    // The guard is on the page as well as on the button, the same way ShowAgent
    // is: a keyboard shortcut or a later caller must not get in behind them.
    [AvaloniaFact]
    public void The_community_page_will_not_open()
    {
        try
        {
            var w = Offline();
            w.ShowCommunityPage();
            Assert.False(w.GetVisualDescendants().OfType<DockPanel>()
                .First(p => p.Name == "CommunityPage").IsVisible);
            w.Close();
        }
        finally { NetworkFeature.Enabled = true; }
    }

    // The load-bearing one. Not "no request was sent": no client exists to
    // send one with, and asking for it is a bug rather than a fallback.
    [AvaloniaFact]
    public void No_http_client_is_ever_built()
    {
        try
        {
            var w = Offline();
            Assert.Throws<InvalidOperationException>(() => w.HttpClient);
            Assert.False(w.DriveConnected);
            w.Close();
        }
        finally { NetworkFeature.Enabled = true; }
    }

    [AvaloniaFact]
    public void Signing_in_to_google_does_nothing()
    {
        try
        {
            var w = Offline();
            Assert.False(NetworkFeature.Enabled);
            Assert.False(w.ConnectGoogleAsync().GetAwaiter().GetResult());
            w.Close();
        }
        finally { NetworkFeature.Enabled = true; }
    }

    // Analytics are a network call, so they go with the rest, and so does the
    // question about them: consent to something that cannot happen is a dialog
    // in the way of a clinician with a client waiting.
    [AvaloniaFact]
    public void Analytics_are_off_and_unaskable()
    {
        try
        {
            NetworkFeature.Enabled = false;
            Assert.True(Telemetry.DisabledByEnvironment);
        }
        finally { NetworkFeature.Enabled = true; }
    }

    [AvaloniaFact]
    public void Settings_offers_no_backup_no_update_check_and_no_feedback()
    {
        try
        {
            var w = Offline();
            w.ShowSettingsPage();
            w.UpdateLayout();
            var words = w.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? "").ToList();
            Assert.DoesNotContain(Strings.Settings_Backup, words);
            Assert.DoesNotContain(Strings.Settings_Updates, words);
            Assert.DoesNotContain(Strings.Settings_FeedbackLabel, words);
            w.Close();
        }
        finally { NetworkFeature.Enabled = true; }
    }

    // The free app is the reason this defaults on. A change here that quietly
    // flipped it would take Community and backup away from everybody.
    [AvaloniaFact]
    public void The_free_app_still_has_all_of_it()
    {
        Assert.True(NetworkFeature.Enabled);
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        Assert.True(Named(w, "HomeCommunityButton")!.IsVisible);
        Assert.True(Named(w, "ShellCommunityButton")!.IsVisible);
        Assert.True(Named(w, "ShareButton")!.IsVisible);
        Assert.NotNull(w.HttpClient);
        w.Close();
    }
}
