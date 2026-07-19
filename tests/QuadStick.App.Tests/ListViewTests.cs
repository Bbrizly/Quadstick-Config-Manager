using Avalonia.Automation;
using Avalonia.Controls;
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

public class ListViewTests
{
    // The tester added an input with "+ input", typed a value, and got no
    // remove (trash) button until switching views and back. The row must
    // rebuild on commit so the remove button is there right away, and
    // clicking it must really take the input out of the file.
    [AvaloniaFact]
    public void A_newly_typed_input_gets_a_working_remove_button_immediately()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.NewFromTemplate("smoke.csv");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);
        w.UpdateLayout(); // realize the list rows inside the scroll viewer

        Button Find(string name) => w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == name);
        bool Exists(string name) => w.GetVisualDescendants().OfType<Button>()
            .Any(b => AutomationProperties.GetName(b) == name);

        var addInput = w.GetVisualDescendants().OfType<Button>()
            .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Add another input to row "));
        int row = int.Parse(AutomationProperties.GetName(addInput)!.Split(' ')[^1]);
        addInput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var newBox = w.GetVisualDescendants().OfType<AutoCompleteBox>()
            .First(b => AutomationProperties.GetName(b) == $"Input 2 for row {row}");
        newBox.Text = "left_puff";
        // Commit fires when focus leaves the box, exactly like a user
        // clicking elsewhere. Park focus outside the rows so the rebuild
        // never destroys the focused control.
        w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddRowButton").Focus();
        Dispatcher.UIThread.RunJobs(); // the rebuild is deferred out of the event
        w.UpdateLayout();

        Assert.True(Exists($"Remove input 2 from row {row}")); // no view switch needed

        Find($"Remove input 2 from row {row}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(Exists($"Remove input 2 from row {row}")); // really removed from the file

        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.Close();
    }

    // The Preferences sheet must show the official template's Units and
    // Description columns; hiding them hid the tester's own setting notes.
    [AvaloniaFact]
    public void Preferences_sheet_shows_units_and_description()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = ProfileFile.Load(
            "Profile Name,,Solo\n" +
            "game.csv\n" +
            "Outputs,Function,usb\n" +
            "x,normal,lip\n" +
            "Preferences\n" +
            "\n" +
            "Preference,Value,Units,Description\n" +
            "mouse_speed,201,,how fast the pointer moves\n");
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(false);

        var picker = w.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "SheetPicker");
        picker.SelectedIndex = 1; // the Preferences sheet
        w.UpdateLayout();

        Assert.Contains(w.GetVisualDescendants().OfType<AutoCompleteBox>(),
            b => AutomationProperties.GetName(b) == "Units for row 8");
        var desc = w.GetVisualDescendants().OfType<TextBox>()
            .First(t => (AutomationProperties.GetName(t) ?? "").StartsWith("Description for row 8"));
        Assert.Equal("how fast the pointer moves", desc.Text);

        file.Dirty = false;
        w.Close();
    }
}
