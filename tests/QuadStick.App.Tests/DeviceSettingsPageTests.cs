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

    static MainWindow Open(string prefs = Prefs)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.ShowDeviceSettingsForPreview(prefs);
        w.UpdateLayout();
        return w;
    }

    static T Named<T>(MainWindow w, string name) where T : Control =>
        w.FindControl<StackPanel>("DevicePageBody")!.GetVisualDescendants().OfType<T>()
            .First(c => AutomationProperties.GetName(c) == name);

    static bool Has<T>(MainWindow w, string name) where T : Control =>
        w.FindControl<StackPanel>("DevicePageBody")!.GetVisualDescendants().OfType<T>()
            .Any(c => AutomationProperties.GetName(c) == name);

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
        var body = w.FindControl<StackPanel>("DevicePageBody")!;
        Assert.Contains(body.GetVisualDescendants().OfType<TextBlock>(),
            t => (t.Text ?? "").Contains("The device uses 75 until you change it"));

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
        var body = w.FindControl<StackPanel>("DevicePageBody")!;
        // The page's own controls, not the boxes a NumericUpDown builds inside
        // its own template: those are named by the control that owns them.
        var controls = body.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c is Slider or NumericUpDown or CheckBox or ComboBox or TextBox)
            .Where(c => c.TemplatedParent is null)
            .ToList();

        Assert.NotEmpty(controls);
        foreach (var c in controls)
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)),
                $"{c.GetType().Name} on the device page has no name");
        w.Close();
    }

    // Sip and puff thresholds are the settings somebody tunes while sipping, so
    // they are on the page with their sliders, not two tabs away as in QMP.
    [AvaloniaFact]
    public void TheSipAndPuffThresholdsAreOnTheSamePage()
    {
        var w = Open();
        Assert.True(Has<Slider>(w, "Hard sip/puff threshold, 10 to 100"));
        Assert.True(Has<Slider>(w, "Soft sip/puff threshold, 5 to 100"));
        Assert.True(Has<Slider>(w, "Sip/puff maximum pressure, 10 to 100"));
        Assert.True(Has<Slider>(w, "Joystick center dead zone, 0 to 20"));
        w.Close();
    }
}
