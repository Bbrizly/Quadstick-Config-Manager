using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(QuadStick.App.Tests.TestAppBuilder))]

namespace QuadStick.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

// UI smoke tests: build the REAL window headlessly and drive its public
// seams. Every screen and every model must construct without throwing.
// A crash here is a crash a disabled user would have hit; these run in CI
// on every push, so a crash-on-click can never ship unnoticed again.
public class SmokeTests
{
    // The Drive button label lives on its Content StackPanel [dot, label], set
    // in RefreshDriveButton. Read it there, not the visual tree: when Google is
    // not configured the button is hidden, so no visual descendants are realized.
    static string DriveButtonWord(Button b) =>
        ((StackPanel)b.Content!).Children.OfType<TextBlock>().First(t => t.Text != "●").Text!;

    static MainWindow NewWindow()
    {
        // A fresh CI machine has no settings file, which would auto-start
        // the tutorial overlay and wall off the UI mid-test. Pre-mark it seen.
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void Window_opens_on_home_without_throwing()
    {
        var w = NewWindow();
        Assert.Contains("Quadstick: Config Manager", w.Title);
        w.Close();
    }

    // No .csv anywhere the user reads: home cards and the window title show
    // the bare profile name.
    [AvaloniaFact]
    public void Home_cards_show_names_without_csv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcm-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mygame.csv"),
            ProfileFile.NewFromTemplate("mygame.csv").ToCsvText());
        var old = MainWindow.LibraryDir;
        MainWindow.LibraryDir = dir;
        try
        {
            var w = NewWindow();
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("mygame", texts);
            Assert.DoesNotContain("mygame.csv", texts);
            w.Close();
        }
        finally { MainWindow.LibraryDir = old; Directory.Delete(dir, recursive: true); }
    }

    // The profile name box shows just the name; the .csv extension is ours
    // to add. Typing it anyway must not double it up.
    [AvaloniaFact]
    public void Profile_name_shows_without_csv_and_gets_it_back_on_commit()
    {
        var w = NewWindow();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        var box = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FileNameBox");
        var park = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SaveButton");
        Assert.Equal("smoke", box.Text);

        box.Text = "racing";
        box.Focus();
        park.Focus(); // commit fires when focus leaves the box
        Assert.Equal("racing.csv", file.Document.CsvFileName);
        Assert.Equal("racing", box.Text);

        box.Text = "gta.csv";
        box.Focus();
        park.Focus();
        Assert.Equal("gta.csv", file.Document.CsvFileName);
        Assert.Equal("gta", box.Text);

        file.Dirty = false;
        w.Close();
    }

    // A greyed ".csv" sits inside the right edge of the name box so users
    // can see the extension is added for them.
    [AvaloniaFact]
    public void Profile_name_box_shows_csv_suffix_hint()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        var box = w.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FileNameBox");
        var hint = Assert.IsType<TextBlock>(box.InnerRightContent);
        Assert.Equal(".csv", hint.Text);
        w.Close();
    }

    // The editor Share button opens a flyout with the two sharing actions.
    // Presence only; we never trigger a network call.
    [AvaloniaFact]
    public void Editor_share_button_flyout_has_both_actions()
    {
        var w = NewWindow();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        var share = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ShareButton");
        var flyout = Assert.IsType<MenuFlyout>(share.Flyout);
        var headers = flyout.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
        Assert.Contains("Copy share link", headers);
        Assert.Contains("Open in Google Sheets", headers);
        file.Dirty = false;
        w.Close();
    }

    // The home Drive button is the always-present status light: shown on any
    // build that can reach Google (a real client shipped), hidden only on a
    // placeholder build where backup could never work.
    [AvaloniaFact]
    public void Home_drive_button_shows_when_google_is_configured()
    {
        var w = NewWindow();
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        Assert.Equal(GoogleAuth.IsConfigured, drive.IsVisible);
        w.Close();
    }

    // The status word matches the real state: off (no client), green (connected),
    // or yellow (needs sign-in). The coloured dot is the other TextBlock.
    [AvaloniaFact]
    public void Home_drive_button_word_matches_the_state()
    {
        var w = NewWindow();
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        var word = DriveButtonWord(drive);
        var expected = !GoogleAuth.IsConfigured ? "Backup off"
            : w.DriveConnected ? "Backing up to Drive" : "Sign in to back up";
        Assert.Equal(expected, word);
        w.Close();
    }

    // In the yellow (needs sign-in) state one press only arms; the browser opens
    // on the second press, so a stray click never launches sign-in.
    [AvaloniaFact]
    public void Home_drive_button_arms_before_it_signs_in()
    {
        var w = NewWindow();
        if (!GoogleAuth.IsConfigured || w.DriveConnected) { w.Close(); return; } // only the yellow state arms
        var drive = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDriveButton");
        drive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var word = DriveButtonWord(drive);
        Assert.Equal("Press again to sign in", word);
        w.Close();
    }

    [AvaloniaFact]
    public void New_profile_opens_the_editor_and_every_zone_builds()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        Assert.Contains("smoke", w.Title);
        Assert.DoesNotContain(".csv", w.Title); // the extension stays out of sight

        // Selecting every part of the device must never throw, mapped or not.
        foreach (var zone in new[]
                 { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip", "jacks", "other", "unset" })
            w.SelectZoneForPreview(zone);
        w.Close();
    }

    [AvaloniaFact]
    public void Every_model_rebuilds_the_device_view_without_throwing()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        for (int model = 0; model < 3; model++)
        {
            w.SetModelForPreview(model);
            w.SelectZoneForPreview("mp_center");
        }
        w.Close();
    }

    [AvaloniaFact]
    public void Label_style_cycles_through_all_three_without_throwing()
    {
        var w = NewWindow();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        w.SelectZoneForPreview("mp_center");
        // plain English -> Xbox style -> raw list names -> back to plain
        for (int k = 0; k < 3; k++) w.CycleLabelStyleForPreview();
        w.Close();
    }

    [AvaloniaFact]
    public void Save_as_template_then_use_template_round_trips()
    {
        MainWindow.LibraryDir = Path.Combine(Path.GetTempPath(), "qs-tpl-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(MainWindow.TemplatesDir);
            var f = ProfileFile.NewFromTemplate("shooter.csv");
            File.WriteAllText(Path.Combine(MainWindow.TemplatesDir, "My FPS.csv"), f.ToCsvText());

            // A saved template must load back into the editor as a fresh copy.
            var w = NewWindow();
            var loaded = ProfileFile.Load(File.ReadAllText(Path.Combine(MainWindow.TemplatesDir, "My FPS.csv")));
            w.LoadProfile(loaded);
            Assert.NotEmpty(loaded.Document.Sheets);
            loaded.Dirty = false;
            w.Close();
        }
        finally
        { if (Directory.Exists(MainWindow.LibraryDir)) Directory.Delete(MainWindow.LibraryDir, recursive: true); }
    }

    [Theory]
    [InlineData("My FPS", "My FPS.csv")]
    [InlineData("shooter.csv", "shooter.csv")]
    [InlineData("bad/name:v2", "bad_name_v2.csv")]
    [InlineData("   ", "")]
    public void Template_names_are_made_safe(string input, string expected)
        => Assert.Equal(expected, MainWindow.SafeTemplateName(input));

    [AvaloniaFact]
    public void Editing_through_the_file_keeps_the_window_alive()
    {
        var w = NewWindow();
        var f = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(f);
        var b = f.Document.Sheets[0].Bindings[0];
        f.SetCell(b.Row, 0, "circle");          // edit an output
        f.EnsureVersionHeader();                 // the P0 case: rows shift
        w.LoadProfile(f);                        // window must rebind cleanly
        w.SelectZoneForPreview("mp_left");
        Assert.True(f.Document.HasVersionHeader);
        // Closing dirty pops the "save your changes?" dialog, which has no
        // user to answer it headlessly (that guard doing its job is exactly
        // why it hangs). Mark saved so Close proceeds.
        f.Dirty = false;
        w.Close();
    }
}
