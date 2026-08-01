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
        string csv, IReadOnlyList<SkippedTab>? skipped = null, string? limitation = null)
    {
        var owner = NewOwner();
        owner.OpenDeviceProfile(ProfileFile.Load(csv));
        Dispatcher.UIThread.RunJobs();
        var file = owner.OpenFile!;
        var review = new ImportReviewWindow(owner, file, "Their sheet",
            skipped ?? Array.Empty<SkippedTab>(), limitation);
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
}
