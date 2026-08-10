using Avalonia;
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

// The window a user meets right after importing their own spreadsheet. It
// exists because a correct import read as a broken one: the app had taken the
// sheet faithfully, left one tab behind exactly as the device does, and said
// so only in a status line that scrolled away.
//
// So what is pinned here is not layout. It is what the window is willing to
// claim. It must never call a partial read clean, it must name what was left
// out, and every decision it offers has to actually change the file or
// actually leave it alone.
public class ImportReviewWindowTests
{
    // Two modes and a preferences sheet, nothing wrong with any of it.
    const string CleanCsv =
        "Profile Name,,Walking\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_N,normal,right_sip\r\n" +
        "\r\n" +
        "Profile Name,,Driving\r\n,,\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_S,normal,lip\r\n";

    // The real shape of the complaint that started this: a word in an input
    // column that the device has never heard of, in a profile that otherwise
    // works. The user meant it as their own name for the row.
    const string AimCsv =
        "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        "left_trigger,normal,mp_center_sip,,,,,aim\r\n";

    static MainWindow NewOwner()
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

    // The review always edits the profile the editor is showing, so the test
    // opens it there first, exactly as an import does.
    static (MainWindow Owner, ProfileFile File, ImportReviewWindow Review) Open(
        string csv, IReadOnlyList<SkippedTab>? skipped = null, string? limitation = null,
        IReadOnlyList<TabRename>? renamed = null)
    {
        var owner = NewOwner();
        owner.OpenDeviceProfile(ProfileFile.Load(csv));
        Dispatcher.UIThread.RunJobs();
        var file = owner.OpenFile!;
        var review = new ImportReviewWindow(owner, file, "Their sheet",
            skipped ?? Array.Empty<SkippedTab>(), limitation, renamed);
        _ = review.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();
        return (owner, file, review);
    }

    static void Done(MainWindow owner, ImportReviewWindow review)
    {
        review.Close();
        if (owner.OpenFile is not null) owner.OpenFile.Dirty = false; // else Close opens the save dialog
        owner.Close();
    }

    static string[] AllText(Window w)
    {
        w.UpdateLayout();
        return w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
    }

    static bool Says(Window w, string fragment) => AllText(w).Any(t => t.Contains(fragment));

    static Button? Find(Window w, string content) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);

    static void Press(Window w, string content)
    {
        var b = Find(w, content) ?? throw new InvalidOperationException(
            $"No \"{content}\" button. Buttons: {string.Join(", ", w.GetVisualDescendants().OfType<Button>().Select(x => x.Content))}");
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    // A tab of bindings whose A1 was typed over. The device skips it too, so
    // nothing here is being rescued: it is being offered.
    static SkippedTab Dpad() => new("Dpad", new List<string[]>
    {
        new[] { "Those are pretty cool. Sorry I'm a little busy right now." },
        new[] { "dpad.csv" },
        new[] { "PlayStation Outputs", "Function", "usb" },
        new[] { "dpad_N", "normal", "right_sip" },
        new[] { "dpad_S", "normal", "left_sip" },
        new[] { "dpad_E", "normal", "lip" },
    });

    [AvaloniaFact]
    public void A_clean_import_says_so_and_asks_nothing()
    {
        var (owner, _, review) = Open(CleanCsv);

        Assert.True(Says(review, "came in clean"));
        Assert.True(Says(review, "No profile data was skipped"));
        Assert.True(Says(review, "2 modes"));
        // Nothing to decide, so none of the decision buttons exist.
        Assert.Null(Find(review, "Leave it"));
        Assert.Null(Find(review, "Leave it out"));
        // The advanced view is still on offer. That was the point of it.
        Assert.NotNull(Find(review, "Advanced"));

        Done(owner, review);
    }

    // The one this window could get most wrong. A published Google link hands
    // back a single tab and no way to ask for the rest, so "clean" would be a
    // lie about four modes nobody ever saw.
    [AvaloniaFact]
    public void A_partial_read_is_never_called_clean()
    {
        var (owner, _, review) = Open(CleanCsv,
            limitation: "This link is a published one, and a published link only ever gives a single tab.");

        Assert.False(Says(review, "came in clean"));
        Assert.True(Says(review, "Only part of the spreadsheet could be read"));
        Assert.True(Says(review, "only ever gives a single tab"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void A_tab_that_did_not_come_in_is_named_with_what_the_device_does_about_it()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { Dpad() });

        Assert.False(Says(review, "came in clean"));
        Assert.True(Says(review, "1 tab did not come in"));
        Assert.True(Says(review, "\"Dpad\""));
        // The honest half: the QuadStick is not running this tab either.
        Assert.True(Says(review, "Neither one is running it today"));

        Done(owner, review);
    }

    // The tab QMP writes for you to read. It has never been importable and the
    // app used to pass over it without a word, which is half of what a real
    // user reported as "reference card sheet will not import".
    static SkippedTab ReferenceCard() =>
        new("Reference Card", Array.Empty<string[]>(), SkippedTabKind.Helper);

    [AvaloniaFact]
    public void A_helper_tab_is_named_as_a_fact_and_never_offered_as_a_mode()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { ReferenceCard() });

        Assert.True(Says(review, "1 tab is not profile data"));
        Assert.True(Says(review, "\"Reference Card\""));
        Assert.True(Says(review, "never loads it"));
        // No decision, because there is no mode here to take back. Offering one
        // would invent a mode out of documentation.
        Assert.Null(Find(review, "Add it as a working mode"));
        Assert.Null(Find(review, "Leave it out"));

        Done(owner, review);
    }

    // Every workbook QMP writes carries a Reference Card. If one counted against
    // the import, no import would ever be clean and the word would stop meaning
    // anything.
    [AvaloniaFact]
    public void A_helper_tab_does_not_stop_an_import_from_being_clean()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { ReferenceCard() });

        Assert.True(Says(review, "came in clean"));

        Done(owner, review);
    }

    // Two tabs, both named "Left  Joystick" in C1, which is the workbook a real
    // user imported on 2026-08-01. Both modes come in and the device runs both;
    // it counts modes by their order and never reads the name. The report was
    // that only one had imported, because the list showed the same words twice
    // and nothing else.
    const string SameNameCsv =
        "Profile Name,,Left  Joystick\r\ncrewmotorfest.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_N,normal,right_sip\r\n" +
        "\r\n" +
        "Profile Name,,Left  Joystick\r\n,,\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_S,normal,lip\r\n";

    [AvaloniaFact]
    public void Two_modes_sharing_a_name_are_numbered_and_the_repeat_is_explained()
    {
        var (owner, _, review) = Open(SameNameCsv);

        Assert.True(Says(review, "2 modes"));
        // The number is the only thing that tells them apart, so it leads.
        Assert.True(Says(review, "Mode 1: Left  Joystick"));
        Assert.True(Says(review, "Mode 2: Left  Joystick"));
        // And the repeat is named, so the second one does not read as missing.
        Assert.True(Says(review, "More than one mode is named"));
        Assert.True(Says(review, "by their order in the file, not by their name"));

        Done(owner, review);
    }

    // The note is for a real repeat only. Saying it on every import would teach
    // people to skip the one place that explains the thing.
    [AvaloniaFact]
    public void Modes_with_their_own_names_are_still_numbered_but_not_explained()
    {
        var (owner, _, review) = Open(CleanCsv);

        Assert.True(Says(review, "Mode 1: Walking"));
        Assert.True(Says(review, "Mode 2: Driving"));
        Assert.False(Says(review, "More than one mode is named"));

        Done(owner, review);
    }

    // The firmware's dispatch loop increments its profile counter on "Profile"
    // alone. "Preferences" and "Infrared" each run their own reader and leave
    // the counter where it was, so an infrared sheet between two modes does not
    // push the second one to three. Numbering it as a mode would have made
    // every number under it name the wrong mode on the device, which is worse
    // than not numbering at all.
    const string InfraredCsv =
        "Profile Name,,Walking\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_N,normal,right_sip\r\n" +
        "\r\n" +
        "Infrared,Samsung Most Models - Set #: 595,Comments\r\n" +
        ",http://irdb.globalcache.com/\r\nCommand Name,Hex Code\r\n" +
        "ir_tv_on_off,0000 006D 0000 0022 00AA 00AA\r\n" +
        "\r\n" +
        "Profile Name,,Driving\r\n,,\r\nPlayStation Outputs,Function,usb\r\n" +
        "dpad_S,normal,lip\r\n";

    [AvaloniaFact]
    public void An_infrared_sheet_does_not_take_a_mode_number()
    {
        var (owner, _, review) = Open(InfraredCsv);

        Assert.True(Says(review, "Mode 1: Walking"));
        Assert.True(Says(review, "Mode 2: Driving"));
        Assert.False(Says(review, "Mode 3"));
        // Listed all the same. It came in, and a sheet that vanishes from this
        // list is the exact reading the window exists to prevent.
        Assert.True(Says(review, "Infrared commands"));
        // And it is not a mode in the count either.
        Assert.True(Says(review, "2 modes"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Adding_a_skipped_tab_makes_it_a_real_mode_and_says_what_it_changed()
    {
        var (owner, file, review) = Open(CleanCsv, new[] { Dpad() });
        Assert.Equal(2, file.Document.Sheets.Count);

        Press(review, "Add it as a working mode");

        Assert.Equal(3, file.Document.Sheets.Count);
        Assert.Equal("Dpad", file.Document.Sheets[2].ModeName);
        Assert.Equal(3, file.Document.Sheets[2].Bindings.Count);
        // The question is answered, so it stops being asked, and what happened
        // to the user's own A1 text is stated rather than left to be found.
        Assert.Null(Find(review, "Add it as a working mode"));
        Assert.True(Says(review, "is now a mode in this profile"));
        Assert.True(Says(review, "\"Profile Name\" where your text was"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Leaving_a_tab_out_changes_nothing_and_stops_asking()
    {
        var (owner, file, review) = Open(CleanCsv, new[] { Dpad() });

        Press(review, "Leave it out");

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.Null(Find(review, "Add it as a working mode"));
        Assert.True(Says(review, "the same as your QuadStick does today"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void A_word_the_device_does_not_know_can_become_the_rows_own_name()
    {
        var (owner, file, review) = Open(AimCsv);

        Assert.True(Says(review, "H4"));
        Press(review, "Use as this row's name");

        Assert.Equal("aim", file.GetCell(4, ProfileFile.ActionColumn));
        Assert.Equal("", file.GetCell(4, 7));
        Assert.Equal("aim", file.Document.Sheets[0].Bindings[0].ActionName);
        // The binding itself is untouched: this was never about the output.
        Assert.Equal("left_trigger", file.Document.Sheets[0].Bindings[0].Output);
        Assert.True(Says(review, "is now this row's own name"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void The_same_word_can_go_to_the_notes_column_instead()
    {
        var (owner, file, review) = Open(AimCsv);

        Press(review, "Move to notes");

        Assert.Equal("aim", file.GetCell(4, 10));
        Assert.Equal("", file.GetCell(4, 7));
        Assert.True(Says(review, "moved into the notes column"));

        Done(owner, review);
    }

    // "Leave it" is a real answer. The cell keeps its warning in the editor,
    // where a warning belongs, and stops being a question here.
    [AvaloniaFact]
    public void Leaving_a_word_alone_settles_it_without_touching_the_file()
    {
        var (owner, file, review) = Open(AimCsv);

        Press(review, "Leave it");

        Assert.Equal("aim", file.GetCell(4, 7));
        Assert.Null(Find(review, "Use as this row's name"));
        Assert.True(Says(review, "The QuadStick ignores it"));
        // Still a warning where the user edits, which is the honest place.
        Assert.Contains(file.Issues, i => i.Kind == IssueKind.UnknownInput);

        Done(owner, review);
    }

    [AvaloniaFact]
    public void The_newest_change_can_be_undone_from_the_window()
    {
        var (owner, file, review) = Open(AimCsv);

        Press(review, "Use as this row's name");
        Assert.Equal("aim", file.GetCell(4, ProfileFile.ActionColumn));

        Press(review, "Undo");

        Assert.Equal("aim", file.GetCell(4, 7));
        Assert.Equal("", file.GetCell(4, ProfileFile.ActionColumn));
        // And the question comes back rather than vanishing silently.
        Assert.NotNull(Find(review, "Use as this row's name"));

        Done(owner, review);
    }

    // Switching views used to grow the window out from under the pointer. A
    // window that jumps moves every button somebody just found, and it throws
    // away a size they had dragged to fit their screen.
    [AvaloniaFact]
    public void Switching_to_the_advanced_view_leaves_the_window_where_it_was()
    {
        var (owner, _, review) = Open(AimCsv, new[] { Dpad() });
        double w = review.Width, h = review.Height;

        Press(review, "Advanced");
        Assert.Equal(w, review.Width);
        Assert.Equal(h, review.Height);

        Press(review, "Simple view");
        Assert.Equal(w, review.Width);
        Assert.Equal(h, review.Height);

        Done(owner, review);
    }

    [AvaloniaFact]
    public void The_advanced_view_shows_the_grid_and_teaches_the_columns()
    {
        var (owner, _, review) = Open(AimCsv, new[] { Dpad() });

        Press(review, "Advanced");

        Assert.True(Says(review, "A  output"));
        Assert.True(Says(review, "C to J  inputs"));
        Assert.True(Says(review, "K  note   L  your name for the row   M on  never read"));
        // The user's own cells, and the tab that was left out, both on screen.
        Assert.True(Says(review, "left_trigger"));
        Assert.True(Says(review, "dpad_N"));
        Assert.NotNull(Find(review, "Simple view"));

        Done(owner, review);
    }

    // The advanced view draws each left-out tab under a heading that says why
    // it was left out. A helper tab was never left out over its A1 and carries
    // no cells, so that heading was a false reason above an empty grid offered
    // as proof of it. Named once in the simple view is the whole story.
    [AvaloniaFact]
    public void The_advanced_view_does_not_blame_a_helper_tab_on_its_A1()
    {
        var (owner, _, review) = Open(AimCsv, new[] { ReferenceCard(), Dpad() });

        Press(review, "Advanced");

        Assert.False(Says(review, "\"Reference Card\", left out because cell A1"));
        // The tab that really was left out over its A1 still says so.
        Assert.True(Says(review, "\"Dpad\", left out because cell A1"));
        Assert.True(Says(review, "dpad_N"));

        Done(owner, review);
    }

    // The advanced view reuses its controls between rebuilds and only makes them
    // again when the shape changes. It counted every skipped tab against the
    // grids it had drawn, and it no longer draws helper tabs, so a reference
    // card plus a repaired tab made the two numbers agree by accident: the tab
    // was a mode now and was still on screen under "left out".
    // Long enough that the advanced grid is already at its display cap, so the
    // row count cannot notice the repair and the skipped-tab count is the only
    // signal left. That is the case the count was getting wrong.
    static string LongCsv() =>
        "Profile Name,,Left joy\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
        string.Concat(Enumerable.Range(0, 500).Select(_ => "left_trigger,normal,mp_center_sip\r\n"));

    [AvaloniaFact]
    public void A_repaired_tab_leaves_the_advanced_view_even_with_a_helper_beside_it()
    {
        var (owner, file, review) = Open(LongCsv(), new[] { ReferenceCard(), Dpad() });

        Press(review, "Advanced");
        Assert.True(Says(review, "\"Dpad\", left out because cell A1"));

        Press(review, "Add it as a working mode");

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.False(Says(review, "\"Dpad\", left out because cell A1"));

        Done(owner, review);
    }

    // Colour cannot carry any of this. Every cell says where it is, what it
    // holds, and what the QuadStick does with it, so the view works read aloud.
    [AvaloniaFact]
    public void Every_advanced_cell_describes_itself_without_colour()
    {
        var (owner, _, review) = Open(AimCsv);

        Press(review, "Advanced");
        var names = review.GetVisualDescendants().OfType<Border>()
            .Select(AutomationProperties.GetName).Where(n => !string.IsNullOrEmpty(n)).ToList();

        Assert.Contains(names, n => n == "A4, \"left_trigger\", output");
        Assert.Contains(names, n => n == "C4, \"mp_center_sip\", input");
        Assert.Contains(names, n => n == "H4, \"aim\", input, the QuadStick does not know this word");
        Assert.Contains(names, n => n!.StartsWith("K4, empty, note, never read"));

        Done(owner, review);
    }

    // Undoing the file is only half of undoing the decision. The tab left the
    // skipped list when it was added, and if it does not go back the window
    // shows a profile without it and says nothing about where it went.
    [AvaloniaFact]
    public void Undoing_an_added_tab_puts_the_question_back_too()
    {
        var (owner, file, review) = Open(CleanCsv, new[] { Dpad() });

        Press(review, "Add it as a working mode");
        Assert.Equal(3, file.Document.Sheets.Count);

        Press(review, "Undo");

        Assert.Equal(2, file.Document.Sheets.Count);
        Assert.True(Says(review, "1 tab did not come in"));
        Assert.NotNull(Find(review, "Add it as a working mode"));

        Done(owner, review);
    }

    // ---- editing the grid ----
    //
    // The advanced view is the only place in the app that can say "this word is
    // in the wrong column", so it is the only place that can fix it. What is
    // pinned here is that a fix actually reaches the file, that the window stops
    // complaining once it has, and that nothing offers to edit rows that are not
    // in the profile at all.

    const int H = 7;

    static Border GridHost(Window w) =>
        w.GetVisualDescendants().OfType<Border>().First(b =>
            (AutomationProperties.GetName(b) ?? "").StartsWith("Your spreadsheet."));

    static TextBox CellBox(Window w) =>
        w.GetVisualDescendants().OfType<TextBox>().First(t =>
            (AutomationProperties.GetName(t) ?? "").StartsWith("Contents of cell"));

    // Walk the selection from A1, which is where focus lands, to a named cell.
    static void GoTo(Window w, int row, int col)
    {
        GridHost(w).Focus();
        Dispatcher.UIThread.RunJobs();
        for (int i = 1; i < row; i++) w.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        for (int i = 0; i < col; i++) w.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_keyboard_can_pick_a_cell_and_the_window_says_what_it_is()
    {
        var (owner, _, review) = Open(AimCsv);
        Press(review, "Advanced");

        GoTo(review, 4, H);

        // The reference, the column's job, and the reason it is flagged.
        Assert.True(Says(review, "H4"));
        Assert.True(Says(review, "input"));
        Assert.Equal("aim", CellBox(review).Text);

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Moving_a_stray_word_out_of_an_input_column_settles_its_warning()
    {
        var (owner, file, review) = Open(AimCsv);
        Press(review, "Advanced");
        GoTo(review, 4, H);

        Press(review, "Move this to the note column");

        Assert.Equal("", file.GetCell(4, H));
        Assert.Equal("aim", file.GetCell(4, ProfileFile.NoteColumn));
        // The row still does exactly what it did on the device, and the window
        // has stopped asking about it.
        Assert.Equal(new[] { "mp_center_sip" }, file.Document.Sheets[0].Bindings[0].Inputs);
        Assert.DoesNotContain(file.Issues, i => i.Kind == IssueKind.UnknownInput);
        Assert.True(Says(review, "Moved \"aim\" from H4 into the note column."));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Typing_a_new_value_into_the_cell_box_reaches_the_file()
    {
        var (owner, file, review) = Open(AimCsv);
        Press(review, "Advanced");
        GoTo(review, 4, H);

        var box = CellBox(review);
        box.Text = "lip";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();

        // A real input name, so the device now reads two of them on this row.
        Assert.Equal("lip", file.GetCell(4, H));
        Assert.Equal(new[] { "mp_center_sip", "lip" }, file.Document.Sheets[0].Bindings[0].Inputs);
        Assert.DoesNotContain(file.Issues, i => i.Kind == IssueKind.UnknownInput);

        Done(owner, review);
    }

    [AvaloniaFact]
    public void Delete_empties_the_picked_cell_and_Undo_puts_it_back()
    {
        var (owner, file, review) = Open(AimCsv);
        Press(review, "Advanced");
        GoTo(review, 4, H);

        review.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();
        Assert.Equal("", file.GetCell(4, H));
        Assert.True(Says(review, "Emptied H4."));

        Press(review, "Undo");
        Assert.Equal("aim", file.GetCell(4, H));

        Done(owner, review);
    }

    // Editing is only safe if the worst edit is survivable. Typing over the
    // keyword that opens the only mode leaves a profile with no modes at all,
    // which nothing else in the app can produce, so nothing else had to cope
    // with it. The grid can, so everything downstream has to.
    [AvaloniaFact]
    public void Wiping_the_last_mode_keyword_is_survivable_and_undoable()
    {
        var (owner, file, review) = Open(AimCsv);
        Press(review, "Advanced");
        GoTo(review, 1, 0);

        // A1 says "Profile Name". Without it there is no mode left.
        var box = CellBox(review);
        box.Text = "my notes";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
        Dispatcher.UIThread.RunJobs();
        review.UpdateLayout();

        Assert.Empty(file.Document.Sheets);
        // And the way back is right there.
        Press(review, "Undo");
        Assert.Single(file.Document.Sheets);
        Assert.Equal("Profile Name", file.GetCell(1, 0));

        Done(owner, review);
    }

    // The same cell, before it is touched: the window has to say what it is,
    // because "output" is what its column would mean on a binding row and that
    // is exactly the wrong thing to believe here.
    [AvaloniaFact]
    public void The_cell_that_opens_a_mode_says_so_instead_of_calling_itself_an_output()
    {
        var (owner, _, review) = Open(AimCsv);
        Press(review, "Advanced");
        GoTo(review, 1, 0);

        Assert.True(Says(review, "the word that makes this a mode"));
        Assert.False(Says(review, "A1   output"));

        Done(owner, review);
    }

    // A settings sheet reuses columns A and B for something else entirely. If
    // the window called B "function" there, it would be inviting somebody to
    // drag away the value of a setting on the promise that nothing reads it.
    [AvaloniaFact]
    public void A_settings_value_is_not_described_as_a_function()
    {
        var (owner, _, review) = Open(
            "Profile Name,,Walking\r\ngame.csv\r\nPlayStation Outputs,Function,usb\r\n" +
            "dpad_N,normal,right_sip\r\n" +
            "\r\n" +
            "Preferences\r\nprefs.csv\r\nPreference,Value,Units,Description\r\n" +
            "mouse_speed,60\r\n");
        Press(review, "Advanced");

        GoTo(review, 9, 1);
        Assert.Equal("60", CellBox(review).Text);
        Assert.True(Says(review, "the setting's value"));

        Done(owner, review);
    }

    [AvaloniaFact]
    public void A_tab_that_did_not_import_is_shown_but_never_editable()
    {
        var (owner, _, review) = Open(AimCsv, new[] { Dpad() });
        Press(review, "Advanced");

        // Its rows are on screen, so the user can see what was left behind.
        Assert.True(Says(review, "dpad_N"));
        // But nothing in it accepts a drop, because none of it is in the file.
        var leftOut = review.GetVisualDescendants().OfType<Border>().Where(b =>
            (AutomationProperties.GetName(b) ?? "").Contains("this tab was left out")).ToList();
        Assert.NotEmpty(leftOut);
        Assert.All(leftOut, b => Assert.False(DragDrop.GetAllowDrop(b)));

        Done(owner, review);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(7, "H")]
    [InlineData(11, "L")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    public void Column_letters_match_the_spreadsheet_and_survive_a_round_trip(int col, string letter)
    {
        Assert.Equal(letter, ImportReviewWindow.ColumnLetter(col));
        Assert.Equal((24, col), ImportReviewWindow.ParseCell($"{letter}24"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("H")]
    [InlineData("24")]
    [InlineData("the whole file")]
    public void Text_that_does_not_name_a_cell_is_not_read_as_one(string reference) =>
        Assert.Equal(0, ImportReviewWindow.ParseCell(reference).Row);

    // Only the grid edit path used to rebuild, so a decision taken with the
    // grid open left it showing the answer to the question before. The tab's
    // rows went on sitting under "left out because cell A1 does not name a kind
    // of sheet" after the user had just added it as a mode, which is a plain
    // untruth about what is now in their profile, and toggling the view did not
    // clear it either.
    [AvaloniaFact]
    public void A_decision_taken_with_the_grid_open_repaints_the_grid()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { Dpad() });
        Press(review, "Advanced");
        Assert.True(Says(review, "left out because cell A1"));

        Press(review, "Add it as a working mode");

        Assert.False(Says(review, "left out because cell A1"),
            "the grid still calls a mode that is now in the profile a tab that was left out");
        Done(owner, review);
    }

    // Build clears the panel, which destroys the button that was just pressed.
    // Nothing put focus anywhere afterwards, so the next Tab restarted from the
    // top of the window, and nothing said what had happened either: three of
    // the decisions change neither the heading nor the subheading, so a screen
    // reader user pressed a button and heard silence.
    [AvaloniaFact]
    public void A_decision_says_what_it_did_and_leaves_focus_somewhere_real()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { Dpad() });

        Press(review, "Leave it out");

        Assert.True(Says(review, "was left out"));
        var focused = review.FocusManager?.GetFocusedElement() as Visual;
        Assert.NotNull(focused);
        Assert.Contains(focused!, review.GetVisualDescendants());
        Done(owner, review);
    }

    // The live region is what makes the sentence above reach a screen reader.
    [AvaloniaFact]
    public void What_a_decision_did_is_announced_politely_but_assertively()
    {
        var (owner, _, review) = Open(CleanCsv, new[] { Dpad() });

        Press(review, "Leave it out");

        var announced = review.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => AutomationProperties.GetLiveSetting(t) == AutomationLiveSetting.Assertive)
            .Select(t => t.Text ?? "")
            .ToArray();
        Assert.Contains(announced, t => t.Contains("was left out"));
        Done(owner, review);
    }

    // An answer that changed nothing used to retire the Undo offered for a real
    // change made a moment before, whose snapshot was still the newest on the
    // stack. The affordance vanished and the only way back was Ctrl+Z in the
    // editor behind the window.
    [AvaloniaFact]
    public void An_answer_that_changed_nothing_keeps_the_undo_for_one_that_did()
    {
        var (owner, _, review) = Open(AimCsv, new[] { Dpad() });

        Press(review, "Move to notes");   // a real change, Undo appears
        Assert.NotNull(Find(review, "Undo"));
        Press(review, "Leave it out");    // an answer that changes nothing

        Assert.NotNull(Find(review, "Undo"));
        Done(owner, review);
    }

    // A mode named after its sheet tab is a name the app put there rather than
    // read. Not a loss, so the import is still clean, but never quiet: the name
    // is the whole of how a user picks a mode out of a list.
    [AvaloniaFact]
    public void A_mode_named_from_its_tab_is_said_out_loud()
    {
        var (owner, _, review) = Open(CleanCsv, renamed: new[]
        {
            new TabRename(2, "Driving", "Left Joystick"),
        });

        Assert.True(Says(review, "named after"));
        Assert.True(Says(review, "\"Driving\""));
        Assert.True(Says(review, "\"Left Joystick\""));
        Done(owner, review);
    }

    [AvaloniaFact]
    public void A_mode_whose_name_cell_was_empty_says_that_instead()
    {
        var (owner, _, review) = Open(CleanCsv, renamed: new[] { new TabRename(1, "Menu", "") });

        Assert.True(Says(review, "name cell was empty"));
        Done(owner, review);
    }
}
