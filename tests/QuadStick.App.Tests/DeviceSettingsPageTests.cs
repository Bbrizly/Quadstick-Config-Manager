using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The Device page is where somebody tunes the hardware they talk through, so
// the two rules it has to keep are the two the official manager breaks: a value
// nobody touched is never rewritten, and nothing reaches the device until it is
// asked for.
//
// The third rule is this page's own. Sixty one settings in one scrolling column
// is not a screen anybody can use, so one group is on screen at a time and the
// picture of the device stays put above it.
public class DeviceSettingsPageTests
{
    const string Header = "Preferences\nprefs.csv\nPreference,Value,Units,Description\n";

    // A real prefs.csv is one row per setting and never all of them.
    const string Prefs = Header
        + "volume,40,,\n"
        + "mouse_speed,100,,\n"
        + "sip_puff_threshold,40,,\n"
        + "joystick_deflection_maximum,25,,\n"
        + "deflection_multiplier_up,140,,\n";

    static MainWindow Open(string prefs = Prefs, string category = "Sound and lights")
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.ShowDeviceSettingsForPreview(prefs, category: category);
        w.UpdateLayout();
        return w;
    }

    static MainWindow Detached(string category = "Sound and lights")
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.ShowDeviceSettingsForPreview(root: null, category: category);
        w.UpdateLayout();
        return w;
    }

    static IEnumerable<Control> Body(MainWindow w) =>
        w.FindControl<Panel>("DevicePageBody")!.GetVisualDescendants().OfType<Control>();

    static T Named<T>(MainWindow w, string name) where T : Control =>
        Body(w).OfType<T>().First(c => AutomationProperties.GetName(c) == name);

    static bool Has<T>(MainWindow w, string name) where T : Control =>
        Body(w).OfType<T>().Any(c => AutomationProperties.GetName(c) == name);

    static string[] Said(MainWindow w) =>
        Body(w).OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();

    static string Cell(MainWindow w, string setting)
    {
        var file = w.DevicePrefsForPreview!;
        var sheet = file.Document.Sheets.First(s => s.Type == SheetType.Preferences);
        var row = sheet.Bindings.First(b => b.Output == setting).Row;
        return file.GetCell(row, 1);
    }

    // The Device tab used to open the file manager in a dialog. Managing files
    // and tuning the device are two jobs, and only one of them is the page.
    [AvaloniaFact]
    public void TheDeviceTabShowsTheSettingsPage()
    {
        var w = Open();
        Assert.True(w.FindControl<DockPanel>("DevicePage")!.IsVisible);
        Assert.False(w.FindControl<DockPanel>("HomeView")!.IsVisible);
        Assert.Contains("active", w.FindControl<Button>("ShellDeviceButton")!.Classes);
        w.Close();
    }

    // Drew asked for sliders. A slider on its own says a setting is "about
    // here", so the exact number is beside it and is typeable.
    [AvaloniaFact]
    public void ABoundedSettingGetsASliderAndTheNumberBesideIt()
    {
        var w = Open();
        var slider = Named<Slider>(w, "Speaker volume, 0 to 100");
        var box = Named<NumericUpDown>(w, "Speaker volume");

        Assert.Equal(0, slider.Minimum);
        Assert.Equal(100, slider.Maximum);
        Assert.Equal(40, slider.Value);
        Assert.Equal(40m, box.Value);
        w.Close();
    }

    // Either control drives the setting, and each shows what the other did.
    [AvaloniaFact]
    public void TheSliderAndTheNumberStayOnTheSameValue()
    {
        var w = Open();
        var slider = Named<Slider>(w, "Speaker volume, 0 to 100");
        var box = Named<NumericUpDown>(w, "Speaker volume");

        slider.Value = 60;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(60m, box.Value);
        Assert.Equal("60", Cell(w, "volume"));

        box.Value = 15;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(15, slider.Value);
        Assert.Equal("15", Cell(w, "volume"));
        w.Close();
    }

    // The whole point of the page: a number the file already holds is not
    // touched by the page drawing it. QMP's joystick round trip turns a stored
    // 140 into 147 on a save nobody asked for.
    [AvaloniaFact]
    public void DrawingThePageRewritesNothing()
    {
        var w = Open();
        Assert.Equal(ProfileFile.Load(Prefs).ToCsvText(), w.DevicePrefsForPreview!.ToCsvText());
        Assert.Empty(w.ChangedDeviceSettings);
        Assert.False(w.FindControl<Border>("DeviceSaveBar")!.IsVisible);
        w.Close();
    }

    // Walking every group has to be free of edits too, or a person browsing the
    // page has changed their own device by reading it.
    [AvaloniaFact]
    public void OpeningEveryGroupRewritesNothing()
    {
        var w = Open();
        foreach (var category in PreferenceCatalog.Categories)
        {
            w.ShowDeviceCategoryForPreview(category);
            w.UpdateLayout();
        }
        Assert.Equal(ProfileFile.Load(Prefs).ToCsvText(), w.DevicePrefsForPreview!.ToCsvText());
        Assert.Empty(w.ChangedDeviceSettings);
        w.Close();
    }

    // A value no slider could show without changing its spelling keeps a plain
    // box, so an out-of-range setting survives being looked at.
    [AvaloniaFact]
    public void AnOutOfRangeValueKeepsAPlainTextBox()
    {
        var w = Open(Header + "volume,255,,\n");
        Assert.False(Has<Slider>(w, "Speaker volume, 0 to 100"));
        Assert.Equal("255", Named<TextBox>(w, "Speaker volume").Text);
        Assert.Equal("255", Cell(w, "volume"));
        w.Close();
    }

    // The official manager hides the settings its file does not carry, then
    // writes them anyway. This shows them, says what the device is using, and
    // writes one only when it is changed.
    [AvaloniaFact]
    public void ASettingMissingFromTheFileIsShownAndOnlyWrittenWhenChanged()
    {
        var w = Open();
        Assert.Contains(Said(w), t => t.Contains("The device uses 75 until you change it"));

        Named<NumericUpDown>(w, "LED brightness").Value = 30;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("30", Cell(w, "brightness"));
        // The rows that were already there kept their own values.
        Assert.Equal("140", Cell(w, "deflection_multiplier_up"));
        Assert.Equal("40", Cell(w, "volume"));
        w.Close();
    }

    // Nothing goes to the device on its own. The bar that offers the write is
    // the count of what would be written, and a value typed back to what the
    // device already has is not one of them.
    [AvaloniaFact]
    public void SavingIsOfferedOnlyWhenSomethingActuallyChanged()
    {
        var w = Open();
        var bar = w.FindControl<Border>("DeviceSaveBar")!;
        var box = Named<NumericUpDown>(w, "Speaker volume");

        box.Value = 60;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.True(bar.IsVisible);
        Assert.Equal(new[] { "volume" }, w.ChangedDeviceSettings.ToArray());

        box.Value = 40; // back to what the device has
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Empty(w.ChangedDeviceSettings);
        Assert.False(bar.IsVisible);
        w.Close();
    }

    // An edit in one group is still an edit after moving to another and back.
    [AvaloniaFact]
    public void AChangeSurvivesMovingBetweenGroups()
    {
        var w = Open();
        Named<NumericUpDown>(w, "Speaker volume").Value = 60;
        Dispatcher.UIThread.RunJobs();

        w.ShowDeviceCategoryForPreview("Joystick");
        w.UpdateLayout();
        w.ShowDeviceCategoryForPreview("Sound and lights");
        w.UpdateLayout();

        Assert.Equal(new[] { "volume" }, w.ChangedDeviceSettings.ToArray());
        Assert.Equal(60m, Named<NumericUpDown>(w, "Speaker volume").Value);
        w.Close();
    }

    // Undo puts every setting back to the bytes that were read off the device.
    [AvaloniaFact]
    public void UndoPutsTheFileBackToWhatTheDeviceHas()
    {
        var w = Open();
        Named<NumericUpDown>(w, "Speaker volume").Value = 60;
        Named<NumericUpDown>(w, "LED brightness").Value = 30;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, w.ChangedDeviceSettings.Count);

        w.UndoDeviceChangesForPreview();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(w.ChangedDeviceSettings);
        Assert.Equal(ProfileFile.Load(Prefs).ToCsvText(), w.DevicePrefsForPreview!.ToCsvText());
        w.Close();
    }

    // Every explanation QMP has is a tooltip, which a keyboard or screen reader
    // user never sees. Here the control says what it is and the words are text.
    [AvaloniaFact]
    public void EverySettingControlSaysWhatItIs()
    {
        var w = Open();
        foreach (var category in PreferenceCatalog.Categories)
        {
            w.ShowDeviceCategoryForPreview(category);
            w.UpdateLayout();
            // The page's own controls, not the boxes a NumericUpDown builds
            // inside its own template: those are named by the control that
            // owns them, and not the group list, which names itself.
            var controls = Body(w)
                .Where(c => c is Slider or NumericUpDown or CheckBox or ComboBox or TextBox)
                .Where(c => c.TemplatedParent is null)
                .ToList();

            Assert.NotEmpty(controls);
            foreach (var c in controls)
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)),
                    $"{c.GetType().Name} in {category} has no name");
        }
        w.Close();
    }

    // ---- one group at a time ----

    // Every group the catalog has is reachable, in the catalog's own order.
    [AvaloniaFact]
    public void EveryGroupOfSettingsIsListed()
    {
        var w = Open();
        var rail = Body(w).OfType<ListBox>().First();
        var listed = rail.ItemsSource!.Cast<object>().Select(o => o.ToString()).ToArray();
        Assert.Equal(
            PreferenceCatalog.Categories.Select(PreferenceCatalog.CategoryLabel).ToArray(),
            listed);
        w.Close();
    }

    // The reason this page is not a scroll: the settings for the other eight
    // groups are not built at all, so the open group is the whole screen.
    [AvaloniaFact]
    public void OnlyTheOpenGroupIsOnScreen()
    {
        var w = Open(Prefs, "Sound and lights");
        Assert.True(Has<Slider>(w, "Speaker volume, 0 to 100"));
        Assert.False(Has<Slider>(w, "Hard sip/puff threshold, 10 to 100"));

        w.ShowDeviceCategoryForPreview("Sip and puff");
        w.UpdateLayout();
        Assert.True(Has<Slider>(w, "Hard sip/puff threshold, 10 to 100"));
        Assert.False(Has<Slider>(w, "Speaker volume, 0 to 100"));
        w.Close();
    }

    // Sip and puff is one group, so the thresholds somebody tunes while sipping
    // are all on the screen together, not spread over two tabs as in QMP.
    [AvaloniaFact]
    public void TheSipAndPuffThresholdsAreInOneGroup()
    {
        var w = Open(Prefs, "Sip and puff");
        Assert.True(Has<Slider>(w, "Hard sip/puff threshold, 10 to 100"));
        Assert.True(Has<Slider>(w, "Soft sip/puff threshold, 5 to 100"));
        Assert.True(Has<Slider>(w, "Sip/puff maximum pressure, 10 to 100"));
        w.Close();
    }

    // ---- the picture ----

    // The group says which part of the device it changes, in words, because a
    // ring drawn on a photo is a cue somebody has to be able to see.
    [AvaloniaFact]
    public void ThePictureNamesThePartTheGroupChanges()
    {
        var w = Open(Prefs, "Sip and puff");
        Assert.Contains(Said(w), t => t.StartsWith("This group changes:", StringComparison.Ordinal)
            && t.Contains("mouthpiece"));

        w.ShowDeviceCategoryForPreview("Bluetooth");
        w.UpdateLayout();
        Assert.Contains(Said(w), t => t.Contains("nothing you can point at"));
        w.Close();
    }

    // The two joystick settings are a percent of travel each, which is why they
    // are drawn as circles. The circles have to follow the sliders.
    [AvaloniaFact]
    public void TheJoystickPadFollowsTheJoystickSettings()
    {
        var w = Open(Prefs, "Joystick");
        // 8 is the catalog default for the dead zone; 25 is in the file above.
        Assert.Contains(Said(w), t => t.Contains("dead zone ends at 8%") && t.Contains("full signal at 25%"));

        Named<NumericUpDown>(w, "Joystick center dead zone").Value = 15;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Contains(Said(w), t => t.Contains("dead zone ends at 15%"));
        w.Close();
    }

    // With a QuadStick reporting, the pad says where the stick is. Without one
    // it says that, rather than showing a centred stick that is not there.
    [AvaloniaFact]
    public void ThePadSaysWhereTheStickIsOrThatNothingIsReadingIt()
    {
        var w = Open(Prefs, "Joystick");
        Assert.Contains(Said(w), t => t.StartsWith("The stick is not being read", StringComparison.Ordinal));

        w.ShowLiveInputForPreview(new LiveState(0.5, -0.5, Array.Empty<int>(), "QuadStick"));
        w.UpdateLayout();
        Assert.Contains(Said(w), t => t == "The stick is at 50% across and 50% up.");

        w.ShowLiveInputForPreview(null);
        w.UpdateLayout();
        Assert.Contains(Said(w), t => t.StartsWith("The stick is not being read", StringComparison.Ordinal));
        w.Close();
    }

    // ---- nothing plugged in ----

    // The page used to be one sentence telling you to go and find a cable.
    // Every setting is worth reading before you own a stick, and it is the only
    // way to look at this screen on a machine with nothing attached.
    [AvaloniaFact]
    public void WithNoQuadStickThePageStillShowsEverySetting()
    {
        var w = Detached();
        Assert.True(Has<Slider>(w, "Speaker volume, 0 to 100"));

        w.ShowDeviceCategoryForPreview("Sip and puff");
        w.UpdateLayout();
        Assert.True(Has<Slider>(w, "Hard sip/puff threshold, 10 to 100"));

        w.ShowDeviceCategoryForPreview("Joystick");
        w.UpdateLayout();
        Assert.True(Has<NumericUpDown>(w, "Up deflection multiplier"));
        w.Close();
    }

    // And it says so first, in words, at the top.
    [AvaloniaFact]
    public void WithNoQuadStickThePageSaysSoAtTheTop()
    {
        var w = Detached();
        Assert.Contains(Said(w), t => t.StartsWith("No QuadStick is plugged in", StringComparison.Ordinal));
        w.Close();
    }

    // A Save button with nowhere to send the file is a button that lies. The
    // edits are still kept, so plugging in and reloading is not the way to lose
    // them, and the bar says what to do instead.
    [AvaloniaFact]
    public void WithNoQuadStickNothingOffersToSave()
    {
        var w = Detached();
        Named<NumericUpDown>(w, "Speaker volume").Value = 60;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        var bar = w.FindControl<Border>("DeviceSaveBar")!;
        Assert.True(bar.IsVisible);
        Assert.Equal("60", Cell(w, "volume"));
        Assert.DoesNotContain(bar.GetVisualDescendants().OfType<Button>(),
            b => AutomationProperties.GetName(b) == "Write the changed settings to prefs.csv on your QuadStick");
        Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(),
            t => (t.Text ?? "").Contains("Plug in your QuadStick to save"));
        w.Close();
    }
}
