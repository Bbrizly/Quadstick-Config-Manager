using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Share with no Google connection used to open Settings, with a one line
// status behind it. The user was told where to go and nothing about why or
// what came after. This is the wizard that replaced it.
//
// The tests run with no token stored, which is the state the wizard exists
// for, so nothing here reaches the network.
public sealed class ShareSetupWindowTests : IDisposable
{
    // These write to the real settings file, so put back what was there. A
    // test run must not quietly turn off the backup of whoever ran it.
    readonly bool _hadBackup = Settings.Load().DriveBackup;

    public void Dispose()
    {
        var s = Settings.Load();
        s.DriveBackup = _hadBackup;
        Settings.Save(s);
    }

    static MainWindow Owner()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        // The state the wizard exists for. The settings file on the machine
        // running the tests may well have a live Google connection in it, and
        // then these would reach the network and prove nothing.
        s.DriveBackup = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(ProfileFile.NewFromTemplate("smoke.csv"));
        return w;
    }

    static IEnumerable<TextBlock> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>();

    static Button? ButtonSaying(Control root, string content) =>
        root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);

    // Nothing is done yet, and the window says so in words rather than by the
    // colour of a tick.
    [AvaloniaFact]
    public void Every_step_says_where_it_stands_in_words()
    {
        var owner = Owner();
        var wizard = new ShareSetupWindow(owner, "Copy share link", needsSave: true);
        _ = wizard.ShowDialog(owner);

        var lines = Texts(wizard).Select(t => t.Text ?? "").ToList();
        Assert.Contains(lines, l => l.Contains("Step 1 of 3") && l.Contains("Connect Google Drive"));
        Assert.Contains(lines, l => l.Contains("Step 2 of 3") && l.Contains("Save this profile"));
        Assert.Contains(lines, l => l.Contains("Step 3 of 3") && l.Contains("Copy share link"));
        Assert.Contains("Not done yet", lines);

        wizard.Close();
        owner.Close();
    }

    // A profile already on disk has nothing to save, so the wizard is two
    // steps and never asks for one that is already done.
    [AvaloniaFact]
    public void A_saved_profile_gets_two_steps_not_three()
    {
        var owner = Owner();
        var wizard = new ShareSetupWindow(owner, "Copy share link", needsSave: false);
        _ = wizard.ShowDialog(owner);

        var lines = Texts(wizard).Select(t => t.Text ?? "").ToList();
        Assert.Contains(lines, l => l.Contains("Step 1 of 2"));
        Assert.Contains(lines, l => l.Contains("Step 2 of 2") && l.Contains("Copy share link"));
        Assert.DoesNotContain(lines, l => l.Contains("Save this profile"));

        wizard.Close();
        owner.Close();
    }

    // The last step cannot run before the first one, and a disabled button
    // announces nothing on its own, so the reason is in its name.
    [AvaloniaFact]
    public void The_last_step_is_off_until_the_connection_is_made()
    {
        var owner = Owner();
        var wizard = new ShareSetupWindow(owner, "Copy share link", needsSave: false);
        _ = wizard.ShowDialog(owner);

        var finish = ButtonSaying(wizard, "Copy share link")!;
        Assert.False(finish.IsEnabled);
        Assert.Contains("Not available yet", AutomationProperties.GetName(finish));
        Assert.False(wizard.Completed);

        wizard.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_wizard_without_sharing()
    {
        var owner = Owner();
        var wizard = new ShareSetupWindow(owner, "Copy share link", needsSave: false);
        _ = wizard.ShowDialog(owner);

        wizard.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(wizard.IsVisible);
        Assert.False(wizard.Completed);
        owner.Close();
    }

    // Cancelling means the caller does not go on to share.
    [AvaloniaFact]
    public void Cancel_leaves_Completed_false()
    {
        var owner = Owner();
        var wizard = new ShareSetupWindow(owner, "Copy share link", needsSave: false);
        _ = wizard.ShowDialog(owner);

        ButtonSaying(wizard, "Cancel")!.Command?.Execute(null);
        wizard.Close();

        Assert.False(wizard.Completed);
        owner.Close();
    }
}
