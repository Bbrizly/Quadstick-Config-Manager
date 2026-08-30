using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    // Dragging must keep the thumb and its number responsive without making
    // the persisted setting or save bar do work for every pixel of movement.
    // The final value becomes the edit when the pointer is released.
    [AvaloniaFact]
    public void ASliderDragCommitsOnlyWhenReleased()
    {
        var w = Open();
        var slider = Named<Slider>(w, "Speaker volume, 0 to 100");
        var box = Named<NumericUpDown>(w, "Speaker volume");
        var thumb = slider.TranslatePoint(new Point(slider.Bounds.Width * .4,
            slider.Bounds.Height / 2), w)!.Value;

        w.MouseDown(thumb, MouseButton.Left, RawInputModifiers.None);
        slider.Value = 75;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(75m, box.Value);
        Assert.Equal("40", Cell(w, "volume"));
        Assert.Empty(w.ChangedDeviceSettings);
        Assert.False(Save(w).IsEnabled);

        w.MouseUp(thumb, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("75", Cell(w, "volume"));
        Assert.Equal(new[] { "volume" }, w.ChangedDeviceSettings.ToArray());
        Assert.True(Save(w).IsEnabled);
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
        Assert.False(Save(w).IsEnabled);
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

    static Button Undo(MainWindow w) => w.FindControl<Border>("DeviceSaveBar")!
        .GetVisualDescendants().OfType<Button>()
        .First(b => AutomationProperties.GetName(b)
            == "Put every setting back to the value that is on the QuadStick");

    static Button Save(MainWindow w) => w.FindControl<Border>("DeviceSaveBar")!
        .GetVisualDescendants().OfType<Button>()
        .First(b => AutomationProperties.GetName(b)
            == "Write the changed settings to prefs.csv on your QuadStick");

    static string BarSays(MainWindow w) => string.Join(" ",
        w.FindControl<Border>("DeviceSaveBar")!.GetVisualDescendants()
            .OfType<TextBlock>().Select(t => t.Text ?? ""));

    // Nothing goes to the device on its own. Save is offered for a real
    // change and not for a value typed back to what the device already has.
    [AvaloniaFact]
    public void SavingIsOfferedOnlyWhenSomethingActuallyChanged()
    {
        var w = Open();
        var box = Named<NumericUpDown>(w, "Speaker volume");
        Assert.False(Save(w).IsEnabled);

        box.Value = 60;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.True(Save(w).IsEnabled);
        Assert.Equal(new[] { "volume" }, w.ChangedDeviceSettings.ToArray());

        box.Value = 40; // back to what the device has
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.Empty(w.ChangedDeviceSettings);
        Assert.False(Save(w).IsEnabled);
        w.Close();
    }

    // The bar used to appear on the first edit. Appearing is a layout change
    // under whatever the pointer is dragging, which on this page is a slider
    // somebody is holding with a mouth stick. It is always there now and its
    // buttons go from grey to live instead.
    [AvaloniaFact]
    public void TheSaveBarIsThereBeforeAnythingIsChanged()
    {
        var w = Open();
        var bar = w.FindControl<Border>("DeviceSaveBar")!;
        Assert.True(bar.IsVisible);
        double height = bar.Bounds.Height;

        Assert.False(Undo(w).IsEnabled);
        Assert.False(Save(w).IsEnabled);
        Assert.Contains("No changes yet", BarSays(w), StringComparison.Ordinal);

        Named<NumericUpDown>(w, "Speaker volume").Value = 60;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();

        Assert.True(bar.IsVisible);
        Assert.Equal(height, bar.Bounds.Height);
        Assert.True(Undo(w).IsEnabled);
        Assert.True(Save(w).IsEnabled);
        Assert.Contains("1 setting changed", BarSays(w), StringComparison.Ordinal);
        w.Close();
    }

    // The same controls, edit after edit. Rebuilding them on every change is
    // what made a slider drag flicker, and it also threw away the focus of
    // anyone driving the bar from the keyboard.
    [AvaloniaFact]
    public void ChangingASettingDoesNotRebuildTheSaveBar()
    {
        var w = Open();
        var undo = Undo(w);
        var box = Named<NumericUpDown>(w, "Speaker volume");

        for (int v = 41; v <= 50; v++)
        {
            box.Value = v;
            Dispatcher.UIThread.RunJobs();
        }
        w.UpdateLayout();

        Assert.Same(undo, Undo(w));
        Assert.True(undo.IsEnabled);
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

    // The pad is a live preview, not a save-state indicator. A held slider
    // drag must animate its dead-zone ring immediately; Undo must then draw
    // the original setting again.
    [AvaloniaFact]
    public void ASliderDragPreviewsTheJoystickPadAndUndoResetsIt()
    {
        var w = Open(Prefs, "Joystick");
        var slider = Named<Slider>(w, "Joystick center dead zone, 0 to 20");
        var thumb = slider.TranslatePoint(new Point(slider.Bounds.Width * .4,
            slider.Bounds.Height / 2), w)!.Value;

        w.MouseDown(thumb, MouseButton.Left, RawInputModifiers.None);
        slider.Value = 15;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Said(w), t => t.Contains("dead zone ends at 15%"));
        Assert.Empty(w.ChangedDeviceSettings);

        w.MouseUp(thumb, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        w.UndoDeviceChangesForPreview();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(Said(w), t => t.Contains("dead zone ends at 8%"));
        Assert.Empty(w.ChangedDeviceSettings);
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

        Assert.Equal("60", Cell(w, "volume"));
        // Undo works on an edit wherever the edit came from; Save has nowhere
        // to write, so it stays off and the line says what to do about it.
        Assert.True(Undo(w).IsEnabled);
        Assert.False(Save(w).IsEnabled);
        Assert.Contains("Plug in your QuadStick to save", BarSays(w), StringComparison.Ordinal);
        w.Close();
    }

    // A text box asks somebody to already know what the device will accept.
    // The firmware reads every preference but one as a number, so every
    // control on this page is a number, an on/off box or a list of choices.
    // The exception is the bluetooth address, which really is a string.
    [AvaloniaFact]
    public void NoGroupAsksAnybodyToTypeFreeText()
    {
        var w = Open();
        foreach (var category in PreferenceCatalog.Categories)
        {
            w.ShowDeviceCategoryForPreview(category);
            w.UpdateLayout();
            var typed = Body(w).OfType<TextBox>()
                // A NumericUpDown is a TextBox inside a spinner, and its own
                // box is not one of these: it refuses anything but digits.
                .Where(t => t.GetVisualAncestors().OfType<NumericUpDown>().FirstOrDefault() is null)
                .Select(t => AutomationProperties.GetName(t))
                .ToArray();
            Assert.All(typed, name => Assert.Equal("Bluetooth remote address", name));
        }
        w.Close();
    }

    // Every row is at least a click target tall and its control sits in the
    // middle of it, so a label and the thing it names line up whatever height
    // the control happens to be.
    [AvaloniaFact]
    public void EveryRowIsAClickTargetTallAndItsControlIsCentred()
    {
        var w = Open(category: "Joystick");
        double floor = (double)Avalonia.Application.Current!.FindResource("ControlHeight")!;

        var rows = SettingRows(w);
        Assert.Equal(
            PreferenceCatalog.All.Count(d => d.Category == "Joystick"),
            rows.Length); // every setting in the group has a row
        foreach (var line in rows)
        {
            Assert.True(line.Bounds.Height >= floor,
                $"a row is {line.Bounds.Height} tall against a floor of {floor}");

            var control = line.Children.OfType<Control>().Single(c => Grid.GetColumn(c) == 1);
            double middle = control.Bounds.Y + control.Bounds.Height / 2;
            Assert.True(Math.Abs(middle - line.Bounds.Height / 2) < 1.5,
                $"a control sits at {middle} in a row {line.Bounds.Height} tall");
        }
        w.Close();
    }

    // The label-and-control line of each setting: two columns, the label's
    // one a fixed width and the control's a share of what is left.
    static Grid[] SettingRows(MainWindow w) =>
        Body(w).OfType<Grid>()
            .Where(g => g.TemplatedParent is null
                     && g.ColumnDefinitions.Count == 2
                     && g.ColumnDefinitions[0].Width.IsAbsolute
                     && g.ColumnDefinitions[1].Width.IsStar)
            .ToArray();

    // Sideways scrolling on a settings screen means somebody has to drag a
    // bar to read a label. Nothing on this page is allowed to need one.
    [AvaloniaFact]
    public void NothingOnThePageScrollsSideways()
    {
        var w = Open();
        foreach (var category in PreferenceCatalog.Categories)
        {
            w.ShowDeviceCategoryForPreview(category);
            w.UpdateLayout();
            // Only the scrollers this page builds, plus the group list. The
            // ones inside a dropdown or a number box belong to those controls
            // and are the theme's business, not the layout's.
            var mine = Body(w).OfType<ScrollViewer>()
                .Where(s => s.TemplatedParent is null or ListBox)
                .ToArray();
            Assert.NotEmpty(mine);
            foreach (var scroll in mine)
                Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);

            // Turning the bar off only hides it. What proves nothing is cut
            // off is that every row of settings actually fits the panel it is
            // laid out in.
            var panel = mine.Single(s => s.TemplatedParent is null);
            foreach (var line in Body(w).OfType<Grid>()
                         .Where(g => g.GetVisualAncestors().Contains(panel)))
                Assert.True(line.Bounds.Width <= panel.Viewport.Width + 0.5,
                    $"{category}: a row is {line.Bounds.Width} wide in a {panel.Viewport.Width} panel");
        }
        w.Close();
    }

    // A selected row is the app's own blue-grey, wherever the list lives. Only
    // the dialogs used to say so, so every list on a page fell back to the
    // system accent: a saturated fill with white text on it that matched
    // nothing else on the screen.
    [AvaloniaFact]
    public void ThePickedGroupIsTheAppsOwnColour()
    {
        var w = Open(category: "Joystick");
        var row = Body(w).OfType<ListBoxItem>().FirstOrDefault(r => r.IsSelected);
        Assert.NotNull(row);

        var fill = row!.GetVisualDescendants()
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Background;
        // The palette lives in theme dictionaries, so the brush has to be
        // looked up for the variant the window is actually showing.
        Assert.True(row.TryFindResource("SelectionTintBrush", row.ActualThemeVariant, out var found));
        var want = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(found);
        Assert.Equal(want.Color,
            Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fill).Color);
        w.Close();
    }
}
