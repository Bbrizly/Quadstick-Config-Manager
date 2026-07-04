using QuadStick.Format;
using Xunit;

namespace QuadStick.Format.Tests;

public class ProfileFileTests
{
    static string Load(string name) => File.ReadAllText(Path.Combine("corpus", name));

    [Fact]
    public void Comments_beyond_column_J_survive_edit_and_save()
    {
        var f = ProfileFile.Load(Load("default.csv"));
        var selectRow = f.Document.Sheets[0].Bindings.First(b => b.Output == "select").Row;
        f.SetCell(selectRow, 2, "lip"); // change the input
        var saved = f.ToCsvText();
        Assert.Contains("Share or Create", saved);        // column K comment intact
        Assert.Contains("Home or Guide or PS", saved);
        Assert.Contains("select,normal,lip", saved);       // edit applied
    }

    [Fact]
    public void Edits_reflect_in_reparse_and_bad_edits_surface_as_issues()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        var row = f.Document.Sheets[0].Bindings[0].Row;
        f.SetCell(row, 1, "blink");
        Assert.True(f.HasErrors);
        f.SetCell(row, 1, "toggle");
        Assert.False(f.HasErrors);
        Assert.Equal("toggle", f.Document.Sheets[0].Bindings[0].Function);
    }

    [Fact]
    public void Add_and_delete_binding_rows()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        int before = f.Document.Sheets[0].Bindings.Count;

        int newRow = f.AddBindingRow(f.Document.Sheets[0]);
        f.SetCell(newRow, 0, "circle");
        f.SetCell(newRow, 2, "mp_left_sip_soft");
        Assert.Equal(before + 1, f.Document.Sheets[0].Bindings.Count);
        Assert.False(f.HasErrors);

        f.DeleteRow(newRow);
        Assert.Equal(before, f.Document.Sheets[0].Bindings.Count);
    }

    [Fact]
    public void RemoveInput_drops_one_input_and_shifts_the_rest_left()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        int row = f.AddBindingRow(f.Document.Sheets[0]);
        f.SetCell(row, 0, "circle");
        f.SetCell(row, 1, "normal");
        f.SetCell(row, 2, "lip");
        f.SetCell(row, 3, "right_sip");
        var b = f.Document.Sheets[0].Bindings.First(x => x.Row == row);
        Assert.Equal(2, b.Inputs.Count);

        f.RemoveInput(row, 0); // drop the first input
        b = f.Document.Sheets[0].Bindings.First(x => x.Row == row);
        Assert.Single(b.Inputs);
        Assert.Equal("right_sip", b.Inputs[0]); // second input shifted into the first slot
        Assert.False(f.HasErrors);
    }

    [Fact]
    public void New_from_template_clones_factory_default_with_new_name()
    {
        var f = ProfileFile.NewFromTemplate("mygame.csv");
        Assert.Equal("mygame.csv", f.Document.CsvFileName);
        Assert.False(f.Document.IsDefaultConfig);
        Assert.True(f.Document.Sheets[0].Bindings.Count >= 30); // real starting point
        Assert.False(f.HasErrors);
        Assert.False(f.CanUndo); // renaming the template is not undoable into default.csv
    }

    [Fact]
    public void Added_row_is_visible_immediately_and_demands_an_output()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        int before = f.Document.Sheets[0].Bindings.Count;
        f.AddBindingRow(f.Document.Sheets[0]);
        f.Reparse();
        Assert.Equal(before + 1, f.Document.Sheets[0].Bindings.Count);
        Assert.Contains(f.Issues, i => i.Severity == Severity.Error && i.Message.Contains("no output name"));
    }

    [Fact]
    public void Device_style_file_with_version_header_and_three_sheets_parses_clean()
    {
        var f = ProfileFile.Load(Load("device-style.csv"));
        Assert.True(f.Document.HasVersionHeader);
        Assert.Equal(3, f.Document.Sheets.Count);
        Assert.Equal("mygame.csv", f.Document.CsvFileName);
        Assert.Equal(3, f.Document.FileNameCellRow); // shifted by the header line
        Assert.Equal("Mouse", f.Document.Sheets[1].ModeName);
        Assert.Equal(SheetType.Preferences, f.Document.Sheets[2].Type);
        Assert.Empty(f.Issues.Where(i => i.Severity == Severity.Error));

        var multi = f.Document.Sheets[0].Bindings.First(b => b.Output == "left_1");
        Assert.Equal(new[] { "mp_left_sip", "right_puff" }, multi.Inputs);

        f.SetCell(f.Document.FileNameCellRow, 0, "racing.csv");
        Assert.Equal("racing.csv", f.Document.CsvFileName);
    }

    [Fact]
    public void Version_header_ensure_is_idempotent_and_undoable()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        Assert.False(f.Document.HasVersionHeader);

        f.EnsureVersionHeader();
        Assert.True(f.Document.HasVersionHeader);
        Assert.StartsWith("QuadStick Configuration,Version 1.5,,gta", f.ToCsvText());
        Assert.Empty(f.Issues.Where(i => i.Severity == Severity.Error));

        var once = f.ToCsvText();
        f.EnsureVersionHeader(); // second call is a no-op
        Assert.Equal(once, f.ToCsvText());

        Assert.True(f.Undo());
        Assert.False(f.Document.HasVersionHeader);
    }

    [Fact]
    public void RemoveInput_maps_index_to_real_column_when_inputs_have_gaps()
    {
        // Input in column D (col 3) with a blank C: index 0 must remove the
        // REAL input, not the blank, and must not shift the column-K comment.
        var f = ProfileFile.Load(
            "Profile Name,,L\ngame.csv\nOutputs,Function,usb\n" +
            "x,normal,,mp_left_sip,lip,,,,,,my comment\n");
        var b = f.Document.Sheets[0].Bindings[0];
        Assert.Equal(new[] { 3, 4 }, b.InputCols);

        f.RemoveInput(b.Row, 0); // remove mp_left_sip
        b = f.Document.Sheets[0].Bindings[0];
        Assert.Equal(new[] { "lip" }, b.Inputs);
        Assert.Contains("my comment", f.ToCsvText()); // column K untouched
    }

    [Fact]
    public void Undo_after_save_marks_the_file_dirty_again()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        f.SetCell(f.Document.Sheets[0].Bindings[0].Row, 2, "lip");
        f.Dirty = false; // simulate a save
        Assert.True(f.Undo());
        Assert.True(f.Dirty); // memory differs from disk again
    }

    [Fact]
    public void Undo_reverses_edits_adds_and_deletes()
    {
        var f = ProfileFile.Load(Load("gta-mode1.csv"));
        var original = f.ToCsvText();
        var row = f.Document.Sheets[0].Bindings[0].Row;

        f.SetCell(row, 2, "lip");
        Assert.True(f.CanUndo);
        Assert.True(f.Undo());
        Assert.Equal(original, f.ToCsvText());

        f.AddBindingRow(f.Document.Sheets[0]);
        f.Undo();
        Assert.Equal(original, f.ToCsvText());

        f.DeleteRow(row);
        f.Undo();
        Assert.Equal(original, f.ToCsvText());
        Assert.False(f.Undo()); // stack exhausted
    }
}

public class DeviceTests : IDisposable
{
    readonly string _drive = Directory.CreateTempSubdirectory("qscm-drive-").FullName;
    readonly string _backups = Directory.CreateTempSubdirectory("qscm-backup-").FullName;

    public DeviceTests() =>
        File.WriteAllText(Path.Combine(_drive, "default.csv"), "Profile Name,,L\ndefault.csv\nOutputs,Function,usb\n");

    public void Dispose() { Directory.Delete(_drive, true); Directory.Delete(_backups, true); }

    static ProfileFile Valid(string name = "game.csv") =>
        ProfileFile.Load($"Profile Name,,L\n{name}\nOutputs,Function,usb\nx,normal,lip\n");

    [Fact]
    public void Install_writes_file_and_backs_up_existing()
    {
        var existing = Path.Combine(_drive, "game.csv");
        File.WriteAllText(existing, "old");

        var result = Device.Install(Valid(), _drive, _backups);

        Assert.True(File.Exists(result.InstalledPath));
        Assert.Contains("x,normal,lip", File.ReadAllText(result.InstalledPath));
        Assert.NotNull(result.BackupPath);
        Assert.Equal("old", File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public void Install_refuses_profiles_with_errors()
    {
        var bad = ProfileFile.Load("Profile Name,,L\ngame.csv\nOutputs,Function,usb\nx,blink,lip\n");
        Assert.Throws<InvalidOperationException>(() => Device.Install(bad, _drive, _backups));
        Assert.False(File.Exists(Path.Combine(_drive, "game.csv")));
    }

    [Fact]
    public void Install_refuses_default_csv_without_confirmation_then_allows_with_it()
    {
        var f = Valid("default.csv");
        Assert.Throws<InvalidOperationException>(() => Device.Install(f, _drive, _backups));
        var result = Device.Install(f, _drive, _backups, confirmDefaultCsv: true);
        Assert.True(File.Exists(result.InstalledPath));
    }

    [Fact]
    public void Install_adds_the_QMP_version_header_when_missing()
    {
        var result = Device.Install(Valid(), _drive, _backups);
        var firstLine = File.ReadLines(result.InstalledPath).First();
        Assert.StartsWith("QuadStick Configuration,Version 1.5", firstLine);
        // And reloading the installed file round-trips clean.
        var reloaded = ProfileFile.Load(File.ReadAllText(result.InstalledPath));
        Assert.True(reloaded.Document.HasVersionHeader);
        Assert.Empty(reloaded.Issues.Where(i => i.Severity == Severity.Error));
    }

    [Fact]
    public void Install_failure_leaves_the_existing_file_untouched()
    {
        // read-only dir — install must fail without touching the existing file
        if (OperatingSystem.IsWindows()) return;
        var existing = Path.Combine(_drive, "game.csv");
        File.WriteAllText(existing, "old");
        File.SetUnixFileMode(_drive,
            UnixFileMode.UserRead | UnixFileMode.UserExecute); // read-only dir: no create/delete
        try
        {
            Assert.ThrowsAny<Exception>(() => Device.Install(Valid(), _drive, _backups));
            Assert.Equal("old", File.ReadAllText(existing));
            Assert.Empty(Directory.GetFiles(_drive, "*.qscm-tmp"));
        }
        finally
        {
            File.SetUnixFileMode(_drive,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void No_temp_files_left_behind()
    {
        Device.Install(Valid(), _drive, _backups);
        Assert.Empty(Directory.GetFiles(_drive, "*.qscm-tmp"));
    }

    [Fact]
    public void Install_refuses_non_quadstick_folder()
    {
        var empty = Directory.CreateTempSubdirectory("qscm-bad-").FullName;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Device.Install(Valid(), empty, _backups));
            Assert.Empty(Directory.GetFiles(empty));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void Install_does_not_mutate_the_open_file()
    {
        var f = Valid();
        Assert.False(f.Document.HasVersionHeader);
        Assert.False(f.Dirty);
        Device.Install(f, _drive, _backups);
        Assert.False(f.Document.HasVersionHeader);
        Assert.False(f.Dirty);
    }
}

public class SheetsUrlTests
{
    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/142Em6Lyr8zT0f3RNI1rjBw92MQWpehcOAuvU6sHzxds/edit#gid=229002792",
                "https://docs.google.com/spreadsheets/d/142Em6Lyr8zT0f3RNI1rjBw92MQWpehcOAuvU6sHzxds/export?format=csv&gid=229002792")]
    [InlineData("https://docs.google.com/spreadsheets/d/142Em6Lyr8zT0f3RNI1rjBw92MQWpehcOAuvU6sHzxds/edit?usp=sharing",
                "https://docs.google.com/spreadsheets/d/142Em6Lyr8zT0f3RNI1rjBw92MQWpehcOAuvU6sHzxds/export?format=csv")]
    [InlineData("https://docs.google.com/spreadsheet/ccc?key=0ArMVckMP_1T0dDF1RTNrMjBTb0doYnhQTDJEVHV0d0E&usp=sharing",
                "https://docs.google.com/spreadsheets/d/0ArMVckMP_1T0dDF1RTNrMjBTb0doYnhQTDJEVHV0d0E/export?format=csv")]
    public void Recognizes_real_link_shapes(string pasted, string expected)
    {
        Assert.True(SheetsUrl.TryGetCsvExportUrl(pasted, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/d/abc")]
    public void Rejects_non_sheet_links(string pasted)
        => Assert.False(SheetsUrl.TryGetCsvExportUrl(pasted, out _));
}
