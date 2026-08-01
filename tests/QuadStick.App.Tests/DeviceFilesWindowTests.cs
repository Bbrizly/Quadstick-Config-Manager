using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// The window that manages what is already on a QuadStick. It deletes files from
// a disabled person's controller, so the things pinned here are the ones that
// would hurt if they broke: the right drive, the right file, a question before
// anything destructive, a protected file that cannot be reached at all, and a
// guide that does not need eyes.
//
// No test in this file touches a real removable drive. Every "device" is a temp
// folder with a default.csv in it, handed to the window through FindRoots.
public sealed class DeviceFilesWindowTests : IDisposable
{
    const string SheetId = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345678";

    readonly string _dir = Path.Combine(Path.GetTempPath(), $"qcm-device-{Guid.NewGuid():N}");
    readonly string _library;
    readonly string _backups;
    readonly string _oldLibrary = MainWindow.LibraryDir;

    public DeviceFilesWindowTests()
    {
        _library = Path.Combine(_dir, "library");
        _backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(_library);
        MainWindow.LibraryDir = _library;
    }

    public void Dispose()
    {
        MainWindow.LibraryDir = _oldLibrary;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- fixtures ----

    static string Csv(string fileName, string header = "") =>
        (header.Length > 0 ? header + "\r\n" : "")
        + $"Profile Name,,Walking\r\n{fileName}\r\nPlayStation Outputs,Function,usb\r\ndpad_N,normal,right_sip\r\n";

    static string Linked(string fileName) =>
        Csv(fileName, $"QuadStick Configuration,Version 1.5,{SheetId},Racing");

    // A drive the app will accept: default.csv at its root, plus whatever else
    // the test asks for.
    string Root(string name, params string[] files)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "default.csv"), Csv("default.csv"));
        foreach (var f in files)
            File.WriteAllText(Path.Combine(root, f), Csv(f));
        return root;
    }

    // ---- window helpers ----

    static MainWindow NewWindow()
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    // Every question the window asks lands here instead of a nested modal, so a
    // test can read the exact words and answer them.
    sealed class Asked
    {
        public readonly List<(string Title, string Body)> Prompts = new();
        public bool Answer;
        public Task<bool> Handle(string title, string body)
        {
            Prompts.Add((title, body));
            return Task.FromResult(Answer);
        }
    }

    async Task<(DeviceFilesWindow Win, Asked Ask)> OpenAsync(MainWindow owner, params string[] roots)
    {
        var live = roots.ToList();
        return (await OpenAsync(owner, () => live), _lastAsked!);
    }

    Asked? _lastAsked;

    async Task<DeviceFilesWindow> OpenAsync(MainWindow owner, Func<IReadOnlyList<string>> roots)
    {
        var ask = new Asked();
        _lastAsked = ask;
        var win = new DeviceFilesWindow(owner)
        {
            FindRoots = roots,
            BackupDir = _backups,
            Confirm = ask.Handle,
            OpenUri = _ => Task.CompletedTask, // never open a real browser in a test
        };
        _ = win.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        await win.Busy;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return win;
    }

    static Button Button(Window w, string automationName) =>
        w.GetVisualDescendants().OfType<Button>()
            .First(b => AutomationProperties.GetName(b) == automationName);

    static bool HasButton(Window w, string automationName) =>
        w.GetVisualDescendants().OfType<Button>()
            .Any(b => AutomationProperties.GetName(b) == automationName);

    // Buttons here start real background work, so a tap is not finished when
    // RaiseEvent returns. Busy is whatever the tap started.
    static async Task TapAsync(DeviceFilesWindow w, string automationName)
    {
        Button(w, automationName).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await w.Busy;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    static string[] AllText(Window w)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }

    static bool Says(Window w, string fragment) => AllText(w).Any(t => t.Contains(fragment));

    static ListBox ListFor(Window w, string root) =>
        w.GetVisualDescendants().OfType<ListBox>().First(l => (string?)l.Tag == root);

    static string[] NamesIn(Window w, string root)
    {
        w.UpdateLayout();
        return ListFor(w, root).ItemsSource!.Cast<ListBoxItem>()
            .Select(i => ((TextBlock)((StackPanel)i.Content!).Children[0]).Text!)
            .ToArray();
    }

    static string Delete(string file, string root) => $"Delete {file} from the QuadStick at {root}";
    static string Copy(string file, string root) => $"Copy {file} from {root} into your profile library";
    static string Open(string file, string root) => $"Open {file} from {root} in the editor";
    static string Sheet(string file, string root) => $"Open the Google Sheet linked from {file} on {root}";

    // ---- the way in ----

    // DEV-01: the way in is a visible button next to the device heading, not a
    // menu and not a right click.
    [AvaloniaFact]
    public void Home_offers_a_visible_manage_files_button()
    {
        var w = NewWindow();
        var button = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "HomeDeviceFilesButton");
        Assert.True(button.IsVisible);
        Assert.Equal(
            "Manage the profile files on your QuadStick: copy, delete, and see the file selection order",
            AutomationProperties.GetName(button));
        w.Close();
    }

    // ---- grouping ----

    // DEV-01: two plugged-in QuadSticks are two lists. A file on one drive is
    // never shown under the other, so no action can be aimed at the wrong one.
    [AvaloniaFact]
    public async Task Two_drives_render_as_two_groups()
    {
        var one = Root("stick-one", "Racing.csv");
        var two = Root("stick-two", "Shooter.csv", "prefs.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, one, two);

        Assert.Equal(new[] { one, two }, win.Roots);
        Assert.Equal(new[] { "default.csv", "Racing.csv" }, NamesIn(win, one));
        Assert.Equal(new[] { "default.csv", "prefs.csv", "Shooter.csv" }, NamesIn(win, two));
        Assert.True(Says(win, one));
        Assert.True(Says(win, two));
        Assert.True(Says(win, "across 2 QuadStick drives"));

        // The buttons for one drive's file exist only with that drive's path in
        // their name, so there is no ambiguous "Delete Racing.csv" anywhere.
        Assert.True(HasButton(win, Delete("Racing.csv", one)));
        Assert.False(HasButton(win, Delete("Racing.csv", two)));

        win.Close();
        w.Close();
    }

    // A drive that refuses to be read takes its own group down and nothing else.
    [AvaloniaFact]
    public async Task An_unreadable_drive_does_not_take_the_other_one_down()
    {
        var good = Root("stick-good", "Racing.csv");
        var gone = Path.Combine(_dir, "stick-gone");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, good, gone);

        Assert.Equal(new[] { good, gone }, win.Roots);
        Assert.Equal(new[] { "default.csv", "Racing.csv" }, NamesIn(win, good));
        Assert.True(Says(win, "Could not read this drive"));
        Assert.True(Button(win, "Look for QuadStick drives again and reload the file list").IsEnabled);

        win.Close();
        w.Close();
    }

    // ---- accessibility ----

    // CROSS-02: every action says which file on which drive it will touch, so a
    // screen reader user is never guessing between two identical file names.
    [AvaloniaFact]
    public async Task Every_action_names_the_file_and_the_drive()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        foreach (var wanted in new[]
                 {
                     Open("Racing.csv", root),
                     Copy("Racing.csv", root),
                     Sheet("Racing.csv", root),
                     Delete("Racing.csv", root),
                     Open("default.csv", root),
                     Delete("default.csv", root),
                 })
            Assert.True(HasButton(win, wanted), wanted);

        var rows = ListFor(win, root).ItemsSource!.Cast<ListBoxItem>()
            .Select(AutomationProperties.GetName).ToArray();
        Assert.All(rows, n => Assert.Contains(root, n!));
        Assert.Contains(rows, n => n!.StartsWith("Racing.csv on "));
        Assert.Contains(rows, n => n!.Contains("protected, it cannot be deleted"));

        win.Close();
        w.Close();
    }

    // CROSS-02: nothing at all hides behind a right click. A mouth stick cannot
    // open a context menu.
    [AvaloniaFact]
    public async Task No_action_is_hidden_in_a_right_click_menu()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        Assert.All(win.GetVisualDescendants().OfType<Control>(), c => Assert.Null(c.ContextFlyout));
        Assert.All(win.GetVisualDescendants().OfType<Control>(), c => Assert.Null(c.ContextMenu));

        win.Close();
        w.Close();
    }

    // The window is file management on a mounted drive and nothing more. No
    // serial, no HID, no firmware, no rename, no load or run.
    [Fact]
    public void The_window_stays_a_file_manager()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "QuadStick.App", "DeviceFilesWindow.cs"));
        foreach (var banned in new[]
                 { "SerialPort", "HidDevice", "Firmware", "System.IO.Ports", "Bluetooth", "Device.Install" })
            Assert.DoesNotContain(banned, source);
    }

    static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QuadStick.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }

    // ---- protected files ----

    // DEV-05: default.csv and prefs.csv have no working delete anywhere. The
    // button is off, raising its click by hand still deletes nothing, and the
    // core primitive refuses them on its own.
    [AvaloniaFact]
    public async Task Protected_files_cannot_be_deleted_from_the_window_or_the_core()
    {
        var root = Root("stick", "prefs.csv", "Racing.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);
        ask.Answer = true; // say yes to everything, and still delete nothing

        foreach (var name in new[] { "default.csv", "prefs.csv" })
        {
            var button = Button(win, Delete(name, root));
            Assert.False(button.IsEnabled);

            await TapAsync(win, Delete(name, root));

            Assert.Empty(ask.Prompts); // it never even got as far as asking
            Assert.True(File.Exists(Path.Combine(root, name)));
            Assert.True(Says(win, $"{name} on"));
            Assert.True(Says(win, "is protected and cannot be deleted"));

            // And the primitive the button would have called says no too.
            var direct = Assert.Throws<InvalidOperationException>(
                () => Device.DeleteProfile(root, name, _backups));
            Assert.Contains("protected", direct.Message);
            Assert.True(File.Exists(Path.Combine(root, name)));
        }

        // The one file that is not protected still has a live delete, so this is
        // not a window with the delete broken everywhere.
        Assert.True(Button(win, Delete("Racing.csv", root)).IsEnabled);

        win.Close();
        w.Close();
    }

    // ---- delete ----

    // DEV-05: the question names the file and the exact drive, the delete runs
    // through the core primitive, and the backup path comes back in words.
    [AvaloniaFact]
    public async Task Deleting_names_the_file_and_the_drive_and_reports_the_backup()
    {
        var root = Root("stick", "Racing.csv");
        var other = Root("other", "Racing.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root, other);
        ask.Answer = true;

        await TapAsync(win, Delete("Racing.csv", root));

        var (title, body) = Assert.Single(ask.Prompts);
        Assert.Contains("Racing.csv", title);
        Assert.Contains("Racing.csv", body);
        Assert.Contains(root, body);
        Assert.Contains(_backups, body);

        Assert.False(File.Exists(Path.Combine(root, "Racing.csv")));
        var backup = Assert.Single(Directory.GetFiles(_backups));
        Assert.EndsWith("Racing.csv", backup);
        Assert.True(Says(win, backup));
        Assert.True(Says(win, Path.Combine(root, "Racing.csv")));

        // The same-named file on the other drive is untouched, and the list
        // reloaded itself.
        Assert.True(File.Exists(Path.Combine(other, "Racing.csv")));
        Assert.Equal(new[] { "default.csv" }, NamesIn(win, root));
        Assert.Equal(new[] { "default.csv", "Racing.csv" }, NamesIn(win, other));

        win.Close();
        w.Close();
    }

    // Cancel means nothing happens, and the window says so rather than going
    // quiet.
    [AvaloniaFact]
    public async Task Saying_no_to_a_delete_changes_nothing()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);
        ask.Answer = false;

        await TapAsync(win, Delete("Racing.csv", root));

        Assert.Single(ask.Prompts);
        Assert.True(File.Exists(Path.Combine(root, "Racing.csv")));
        Assert.False(Directory.Exists(_backups));
        Assert.True(Says(win, "was not deleted"));

        win.Close();
        w.Close();
    }

    // DEV-07: the stick comes out between the list and the button. The window
    // has to stay a working window and say which file on which drive failed.
    [AvaloniaFact]
    public async Task A_drive_pulled_before_the_delete_gives_a_scoped_message()
    {
        var root = Root("stick", "Racing.csv");
        var kept = Root("kept", "Shooter.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root, kept);
        ask.Answer = true;

        Directory.Delete(root, recursive: true); // the stick comes out

        await TapAsync(win, Delete("Racing.csv", root));

        Assert.True(Says(win, "Could not delete Racing.csv from"));
        Assert.True(Says(win, root));
        Assert.False(Directory.Exists(_backups)); // nothing was backed up either
        Assert.True(Button(win, "Look for QuadStick drives again and reload the file list").IsEnabled);
        Assert.True(Button(win, "Close this window").IsEnabled);
        // The drive that is still there kept its files.
        Assert.Equal(new[] { "default.csv", "Shooter.csv" }, NamesIn(win, kept));

        win.Close();
        w.Close();
    }

    // ---- copy to library ----

    // DEV-03: a copy lands in the library and the device file is not touched.
    [AvaloniaFact]
    public async Task Copying_writes_the_library_and_leaves_the_device_alone()
    {
        var root = Root("stick", "Racing.csv");
        var source = Path.Combine(root, "Racing.csv");
        var before = File.ReadAllBytes(source);
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);

        await TapAsync(win, Copy("Racing.csv", root));

        Assert.Empty(ask.Prompts); // nothing to overwrite, nothing to ask
        var dest = Path.Combine(_library, "Racing.csv");
        Assert.True(File.Exists(dest));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.True(Says(win, dest));
        Assert.True(Says(win, "unchanged"));

        win.Close();
        w.Close();
    }

    // DEV-03: a name that already exists in the library needs an explicit
    // answer. No answer, no write.
    [AvaloniaFact]
    public async Task A_copy_collision_needs_an_explicit_answer()
    {
        var root = Root("stick", "Racing.csv");
        var dest = Path.Combine(_library, "Racing.csv");
        File.WriteAllText(dest, "mine, keep it");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);
        ask.Answer = false;

        await TapAsync(win, Copy("Racing.csv", root));

        var (title, body) = Assert.Single(ask.Prompts);
        Assert.Contains("Racing.csv", title);
        Assert.Contains(dest, body);
        Assert.Contains(root, body);
        Assert.Equal("mine, keep it", File.ReadAllText(dest));
        Assert.True(Says(win, "was not copied"));

        // Say yes and the same button now replaces it.
        ask.Answer = true;
        await TapAsync(win, Copy("Racing.csv", root));

        Assert.Equal(2, ask.Prompts.Count);
        Assert.Equal(File.ReadAllText(Path.Combine(root, "Racing.csv")), File.ReadAllText(dest));

        win.Close();
        w.Close();
    }

    // DEV-03: the library file was not there when the copy started, so nothing
    // was asked. It must still not be overwritten without an answer.
    [AvaloniaFact]
    public async Task A_library_file_that_appears_mid_copy_is_not_replaced()
    {
        var root = Root("stick", "Racing.csv");
        var dest = Path.Combine(_library, "Racing.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);
        ask.Answer = false;

        // The click looks for the library file, finds nothing and asks nothing.
        // The file lands before the write, the way a second drive with the same
        // name or another program can put it there.
        Button(win, Copy("Racing.csv", root))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        File.WriteAllText(dest, "landed while the copy was running");
        await win.Busy;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.Empty(ask.Prompts);
        Assert.Equal("landed while the copy was running", File.ReadAllText(dest));
        Assert.True(Says(win, "turned up in your library"));
        Assert.True(Says(win, dest));

        // Copying again is the way through, and that one asks first.
        await TapAsync(win, Copy("Racing.csv", root));

        Assert.Single(ask.Prompts);
        Assert.Equal("landed while the copy was running", File.ReadAllText(dest));

        win.Close();
        w.Close();
    }

    // ---- linked sheet ----

    // DEV-04: a header that names a sheet gets a live button and the built
    // Google URL. A header that names nothing gets a dead button and a reason.
    [AvaloniaFact]
    public async Task Open_linked_sheet_is_live_only_with_valid_header_metadata()
    {
        var root = Root("stick", "Plain.csv");
        File.WriteAllText(Path.Combine(root, "Linked.csv"), Linked("Linked.csv"));
        File.WriteAllText(Path.Combine(root, "Bad.csv"),
            Csv("Bad.csv", "QuadStick Configuration,Version 1.5,not-a-real-id,Racing"));
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        Assert.False(Button(win, Sheet("Plain.csv", root)).IsEnabled);
        Assert.False(Button(win, Sheet("Bad.csv", root)).IsEnabled);
        Assert.True(Button(win, Sheet("Linked.csv", root)).IsEnabled);

        Uri? opened = null;
        win.OpenUri = uri => { opened = uri; return Task.CompletedTask; };
        await TapAsync(win, Sheet("Linked.csv", root));

        Assert.Equal($"https://docs.google.com/spreadsheets/d/{SheetId}/edit", opened?.ToString());
        Assert.True(Says(win, "in your browser"));

        // The dead ones say why in words, not by looking grey.
        var rows = ListFor(win, root).ItemsSource!.Cast<ListBoxItem>()
            .Select(AutomationProperties.GetName).ToArray();
        Assert.Contains(rows, n => n!.StartsWith("Plain.csv") && n.Contains("no linked Google Sheet in its header"));
        Assert.DoesNotContain(rows, n => n!.StartsWith("Linked.csv") && n.Contains("no linked Google Sheet"));

        win.Close();
        w.Close();
    }

    // ---- the light guide ----

    // DEV-06: what the screen says and what Copy writes are the same words in
    // the same order, and the order is the device's own.
    [AvaloniaFact]
    public async Task The_guide_shown_and_the_guide_copied_are_the_same()
    {
        var root = Root("stick", "prefs.csv", "zebra.csv", "Apple.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        var entries = win.Guide(root);
        // default.csv first, then the game files case-insensitively, and prefs
        // is settings so it is not in the list at all.
        Assert.Equal(new[] { "default.csv", "Apple.csv", "zebra.csv" }, entries.Select(e => e.FileName));
        Assert.Equal(new[] { 1, 2, 3 }, entries.Select(e => e.Number));
        Assert.Equal(Device.LedPattern(1), entries[0].Colors);
        Assert.Equal(new[] { "purple", "grey", "grey", "grey", "grey" }, entries[0].Colors);

        var copied = win.GuideText(root).Split(Environment.NewLine);
        Assert.Equal($"File selection order on {Path.GetFileName(root)} ({root})", copied[0]);
        Assert.Equal(entries.Select(e => e.Line).ToArray(), copied.Skip(1).ToArray());

        // The rows on screen announce the very same lines, so a screen reader
        // and the clipboard cannot drift apart.
        var shown = win.GetVisualDescendants().OfType<WrapPanel>()
            .Select(AutomationProperties.GetName)
            .Where(n => n is not null && n.Contains(": "))
            .ToArray();
        Assert.Equal(copied.Skip(1).ToArray(), shown);

        win.Close();
        w.Close();
    }

    // CROSS-02: the guide never speaks in colour alone. Every light is written
    // out next to its dot, using only the four audited names.
    [AvaloniaFact]
    public async Task The_guide_writes_every_colour_out_in_words()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        var words = AllText(win);
        Assert.Contains("purple", words);
        Assert.Contains("grey", words);
        Assert.All(win.Guide(root).SelectMany(e => e.Colors),
            c => Assert.Contains(c, new[] { "purple", "grey", "blue", "red" }));

        // Five lights per entry, so the guide matches the hardware.
        Assert.All(win.Guide(root), e => Assert.Equal(5, e.Colors.Count));

        win.Close();
        w.Close();
    }

    // Copy puts the displayed guide on the clipboard.
    [AvaloniaFact]
    public async Task Copying_the_guide_puts_the_shown_text_on_the_clipboard()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        await TapAsync(win, $"Copy the file selection guide for {Path.GetFileName(root)} at {root} as text");

        Assert.True(Says(win, "Copied the file selection guide"));
        Assert.Equal(win.GuideText(root), await win.Clipboard!.GetTextAsync());

        win.Close();
        w.Close();
    }

    // ---- refresh ----

    // DEV-07: Refresh looks again rather than trusting the list it drew, and it
    // drops the detection cache so a stick plugged in a second ago shows up.
    [AvaloniaFact]
    public async Task Refresh_looks_again_and_survives_a_drive_that_disappeared()
    {
        var one = Root("stick-one", "Racing.csv");
        var two = Root("stick-two", "Shooter.csv");
        var live = new List<string> { one };
        var w = NewWindow();
        var win = await OpenAsync(w, () => live);

        Assert.Equal(new[] { one }, win.Roots);

        // A second stick goes in, and a new file lands on the first.
        live.Add(two);
        File.WriteAllText(Path.Combine(one, "Later.csv"), Csv("Later.csv"));
        await TapAsync(win, "Look for QuadStick drives again and reload the file list");

        Assert.Equal(new[] { one, two }, win.Roots);
        Assert.Equal(new[] { "default.csv", "Later.csv", "Racing.csv" }, NamesIn(win, one));

        // Both come out. The window says so and stays usable.
        live.Clear();
        await TapAsync(win, "Look for QuadStick drives again and reload the file list");

        Assert.Empty(win.Roots);
        Assert.True(Says(win, "No QuadStick drive is plugged in right now"));
        Assert.True(Button(win, "Look for QuadStick drives again and reload the file list").IsEnabled);

        win.Close();
        w.Close();
    }

    // DEV-07: Refresh has to drop the three second detection cache too, or a
    // stick plugged in a second ago stays hidden behind the last scan.
    [Fact]
    public void Refresh_drops_the_drive_detection_cache()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src", "QuadStick.App", "DeviceFilesWindow.cs"));
        Assert.Contains("Device.InvalidateCandidateCache()", source);
    }

    // A drive scan that throws outright still leaves a window someone can use.
    [AvaloniaFact]
    public async Task A_failing_drive_scan_leaves_a_usable_window()
    {
        var w = NewWindow();
        var win = await OpenAsync(w, () => throw new IOException("the bus went away"));

        Assert.Empty(win.Roots);
        Assert.True(Says(win, "the bus went away"));
        Assert.True(Button(win, "Look for QuadStick drives again and reload the file list").IsEnabled);
        Assert.True(Button(win, "Close this window").IsEnabled);

        win.Close();
        w.Close();
    }

    // ---- open ----

    // A device file opens as a working copy with no save path, so Save goes to
    // the library and only Install writes back to the QuadStick.
    [AvaloniaFact]
    public async Task Opening_a_device_file_opens_a_working_copy_in_the_editor()
    {
        var root = Root("stick", "Racing.csv");
        var w = NewWindow();
        var (win, _) = await OpenAsync(w, root);

        await TapAsync(win, Open("Racing.csv", root));

        Assert.NotNull(w.OpenFile);
        Assert.Equal("Racing.csv", w.OpenFile!.Document.CsvFileName);
        Assert.False(win.IsVisible); // gets out of the way of the editor behind it

        w.OpenFile.Dirty = false;
        w.Close();
    }

    // prefs.csv is the device's own settings, so opening it asks first and the
    // question names the drive it came from.
    [AvaloniaFact]
    public async Task Opening_prefs_asks_first_and_names_the_drive()
    {
        var root = Root("stick", "prefs.csv");
        var w = NewWindow();
        var (win, ask) = await OpenAsync(w, root);
        ask.Answer = false;

        await TapAsync(win, Open("prefs.csv", root));

        var (title, body) = Assert.Single(ask.Prompts);
        Assert.Contains("Edit device preferences?", title);
        Assert.Contains(root, body);
        Assert.Null(w.OpenFile);
        Assert.True(win.IsVisible);
        Assert.True(Says(win, "was not opened"));

        win.Close();
        w.Close();
    }
}
