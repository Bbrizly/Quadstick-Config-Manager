using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// steamybiscuit 09-18: the file name is the slot on the profile switch, so
// Install asks for it against what is on the drive before writing anything.
public class InstallAsTests
{
    static ProfileFile Game(string name) => ProfileFile.Load(
        "Profile Name,,Solo\n" + name + "\n" + "Outputs,Function,usb\n" + "mouse_left,normal,lip\n");

    static (MainWindow W, string Root) Open(string name, params string[] onDrive)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var root = Directory.CreateTempSubdirectory("qscm-installas-").FullName;
        foreach (var f in onDrive.Append("default.csv"))
            File.WriteAllText(Path.Combine(root, f), Game(f).ToCsvText());
        var w = new MainWindow();
        w.Show();
        w.LoadProfile(Game(name));
        return (w, root);
    }

    static Window Dialog(MainWindow w, string title)
    {
        Dispatcher.UIThread.RunJobs();
        return Assert.Single(w.OwnedWindows, o => o.Title == title);
    }

    static string Preview(Window d) => string.Join("\n",
        d.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

    static void Click(Window d, string label) =>
        d.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == label)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void Done(MainWindow w, string root)
    {
        w.OpenFile!.Dirty = false;
        w.Close();
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public void It_says_the_number_and_who_moves_down()
    {
        var (w, root) = Open("mario.csv", "apex.csv", "zelda.csv");
        try
        {
            var asking = w.AskInstallNameForTest(root);
            var d = Dialog(w, Strings.Install_AsTitle);
            var text = Preview(d);
            Assert.Contains("Profile 3 of 4", text);
            Assert.Contains("zelda (3 to 4)", text);

            // Typing a name already there says it replaces it, and nobody moves.
            d.GetVisualDescendants().OfType<TextBox>().Single().Text = "Apex";
            Dispatcher.UIThread.RunJobs();
            text = Preview(d);
            Assert.Contains("Replaces apex", text);
            Assert.DoesNotContain("Moves down", text);

            Click(d, Strings.Install_AsInstall);
            Assert.Equal("Apex.csv", asking.Result);
        }
        finally { Done(w, root); }
    }

    // scan_files skips any name holding "prefs.csv", so the switch never gets there.
    [AvaloniaFact]
    public void A_name_the_switch_never_reaches_cannot_be_installed()
    {
        var (w, root) = Open("mario.csv");
        try
        {
            var asking = w.AskInstallNameForTest(root);
            var d = Dialog(w, Strings.Install_AsTitle);
            d.GetVisualDescendants().OfType<TextBox>().Single().Text = "myprefs";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Strings.Install_AsNotListed, Preview(d));
            var install = d.GetVisualDescendants().OfType<Button>().First(b => (b.Content as string) == Strings.Install_AsInstall);
            Assert.False(install.IsEnabled);
            Click(d, Strings.Main_Cancel);
            Assert.Null(asking.Result);
        }
        finally { Done(w, root); }
    }

    [AvaloniaFact]
    public async Task Cancel_writes_nothing_and_keeps_the_name()
    {
        var (w, root) = Open("mario.csv");
        MainWindow.CandidatesForTest = () => [root];
        try
        {
            var flow = w.RunInstallFlowForTest();
            var d = Dialog(w, Strings.Install_AsTitle);
            d.GetVisualDescendants().OfType<TextBox>().Single().Text = "luigi";
            Click(d, Strings.Main_Cancel);
            await flow;

            Assert.Equal(new[] { "default.csv" }, Directory.GetFiles(root).Select(Path.GetFileName));
            Assert.Equal("mario.csv", w.OpenFile!.Document.CsvFileName);
        }
        finally { MainWindow.CandidatesForTest = null; Done(w, root); }
    }

    // Renamed only once every gate said yes: a no at the default.csv gate
    // leaves the open profile as it was.
    [AvaloniaFact]
    public async Task A_no_at_the_default_gate_leaves_the_profile_unrenamed()
    {
        var (w, root) = Open("mario.csv");
        MainWindow.CandidatesForTest = () => [root];
        try
        {
            var flow = w.RunInstallFlowForTest();
            var d = Dialog(w, Strings.Install_AsTitle);
            d.GetVisualDescendants().OfType<TextBox>().Single().Text = "default";
            Click(d, Strings.Install_AsInstall);
            Click(Dialog(w, Strings.Install_OverwriteDefaultCsv), Strings.Main_Cancel);
            await flow;

            Assert.Equal("mario.csv", w.OpenFile!.Document.CsvFileName);
        }
        finally { MainWindow.CandidatesForTest = null; Done(w, root); }
    }

    [AvaloniaFact]
    public async Task The_chosen_name_is_what_lands_on_the_drive()
    {
        var (w, root) = Open("mario.csv");
        MainWindow.CandidatesForTest = () => [root];
        try
        {
            var flow = w.RunInstallFlowForTest();
            var d = Dialog(w, Strings.Install_AsTitle);
            d.GetVisualDescendants().OfType<TextBox>().Single().Text = "luigi";
            Click(d, Strings.Install_AsInstall);

            // The receipt only has a Close button once the write and the flush wait are done.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            Window? receipt = null;
            while (receipt is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                receipt = w.OwnedWindows.FirstOrDefault(o => o.Title == Strings.Install_InstallingProfile
                    && o.GetVisualDescendants().OfType<Button>().Any(b => (b.Content as string) == Strings.Install_Close));
            }
            Assert.NotNull(receipt);
            Click(receipt!, Strings.Install_Close);
            await flow;

            Assert.True(File.Exists(Path.Combine(root, "luigi.csv")));
            Assert.Equal("luigi.csv", w.OpenFile!.Document.CsvFileName);
        }
        finally { MainWindow.CandidatesForTest = null; Done(w, root); }
    }
}
