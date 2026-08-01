namespace QuadStick.Format;

// Checks a parsed profile against the format rules.
//
// Errors block install, so Error means one thing only: the device would misread
// the FILE, past the row that caused it. A row the device skips, or reads
// differently from what it says, is a Warning: reported, but not standing
// between the user and their profile. Half the public catalog was unusable
// while the two meant the same thing.
//
// Every "the device does X" below is read off the firmware source
// (Configuration.c, FW_VERSION 1476), not inferred. The binding loop is
// Load_Configuration_File_Segment; the four rules that decide severity here:
//
//   output   search_for_keyword(keyword, output_keywords, ..., NONE), then
//            preference_keywords, then `continue` on NONE. The `continue`
//            skips the loop's own i++, so an unrecognized output does not
//            even consume one of the 128 binding slots.
//   function search_for_keyword_with_parameter(..., function_keywords, ..., 0)
//            and 0 is NORMAL, so an unrecognized function IS normal. It reads
//            at most two parameters through atoi and ignores anything past
//            them; atoi of a word is 0.
//   inputs   search_for_keyword(..., input_keywords, ..., NONE) per column,
//            then a shift-down loop that drops every NONE. So a word the
//            device has never heard of is removed, and the real inputs beside
//            it close up and still work.
//   rows     the loop ends at `line_buffer[0] != '\n' && != '\r'`. Rows after
//            a blank line fall back to the outer loop, match no segment
//            keyword, and are skipped.
public static class Validator
{
    static readonly System.Buffers.SearchValues<char> InvalidFileNameChars =
        System.Buffers.SearchValues.Create("/\\:*?\"<>| ");

    public static List<Issue> Validate(ProfileDocument doc)
    {
        var issues = new List<Issue>();
        ValidateFileName(doc, issues);

        int profileSheets = 0;
        foreach (var sheet in doc.Sheets)
        {
            // A Preferences sheet carries "name,value" rows with the value in
            // column B (Fred Davison, 2026-07-08), unlike a mode-sheet
            // preference override, which puts the value in column C. Validate
            // it on its own rules rather than as bindings.
            if (sheet.Type == SheetType.Preferences)
            {
                ValidatePreferencesSheet(sheet, issues);
                continue;
            }
            // Infrared sheets carry IR codes, not bindings; skip them so their
            // rows don't trip binding-vocabulary false errors.
            if (sheet.Type != SheetType.ProfileName) continue;

            // Device limits (Configuration.c): 16 profiles, 128 binding rows
            // per profile. Extras are read and thrown away without a sound.
            if (++profileSheets == 17)
                issues.Add(new Issue(Severity.Warning, $"A{sheet.StartRow}",
                    "The device supports 16 modes; it ignores this mode and any after it.",
                    "Remove modes until there are at most 16."));
            // The binding loop's own i++ sits after the `continue` it takes
            // when the output cell matches neither an output nor a preference
            // keyword, so a blank or misspelled output costs no slot at all.
            // Counting every parsed row instead warned 12 of the 309 public
            // profiles about a limit they were nowhere near.
            var counted = sheet.Bindings
                .Where(b => Vocab.IsKnownOutput(b.Output) || Vocab.PreferenceOverrides.Contains(b.Output))
                .ToList();
            if (counted.Count > 128)
                issues.Add(new Issue(Severity.Warning, $"A{counted[128].Row}",
                    $"This mode has {counted.Count} rows with an output name; the device reads the first 128 and ignores the rest.",
                    "Trim the mode to 128 rows."));

            ValidateChannel(sheet, issues);
            foreach (var b in sheet.Bindings)
            {
                if (IsPreferenceOverride(b))
                {
                    ValidatePreferenceOverride(b, issues);
                    continue;
                }
                ValidateOutput(b, issues);
                ValidateFunction(b, issues);
                ValidateInputs(b, issues);
            }
        }
        return issues;
    }

    // A mode-sheet row whose output cell is a preference name sets that
    // preference for the mode (firmware: output lookup misses, preference
    // lookup hits, next cell is skipped, the cell after that is the value).
    //
    // The increment_value/decrement_value exception is NOT what firmware 1476
    // does. Its function_keywords table has 12 entries and neither of these is
    // among them, and the preference branch is taken on the output name alone,
    // so 1476 would skip the function cell and read column C through atoi: a
    // row like "mouse_speed,increment_value 5,right_sip" sets mouse_speed to 0.
    // Fred's validation endpoint does list both, so a firmware newer than the
    // source at hand may well have them. Left as it is deliberately: no profile
    // in the public catalog uses either keyword, so there is nothing to gain by
    // guessing, and treating them as live bindings is the reading that does not
    // silently zero somebody's mouse speed. Revisit against newer firmware.
    static bool IsPreferenceOverride(Binding b) =>
        Vocab.IsPreferenceOverride(b.Output, b.Function);

    static void ValidatePreferenceOverride(Binding b, List<Issue> issues)
    {
        // The firmware reads the VALUE from the third column (it skips the
        // function column). Files in the wild also carry the value in column
        // B; firmware 1476 would read those as 0, so flag them.
        var valueInC = b.Inputs.Count > 0 && b.InputCols.Count > 0 && b.InputCols[0] == 2
            ? b.Inputs[0] : null;
        if (valueInC != null)
        {
            var rejected = false;
            if (!long.TryParse(valueInC, System.Globalization.NumberStyles.Integer,
                               System.Globalization.CultureInfo.InvariantCulture, out _)
                && !IsWordValuedPreference(b.Output))
            {
                issues.Add(new Issue(Severity.Error, $"C{b.Row}",
                    $"\"{valueInC}\" is not a whole number. \"{b.Output}\" is a device setting; this cell is its value.",
                    "Replace it with a whole number, e.g. \"50\"."));
                rejected = true;
            }
            // A mode override reads its value from column C, so that is the
            // cell the catalog checks point at. The name itself came from
            // Vocab.PreferenceOverrides, so it is never unknown here; a name
            // the app has never heard of is validated as an output instead.
            if (valueInC.Length > 0 && PreferenceCatalog.TryGet(b.Output, out var def))
                ValidateAgainstCatalog(def, valueInC, $"C{b.Row}", rejected, issues);
            return;
        }
        if (b.Function.Length > 0)
        {
            issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                $"\"{b.Output}\" is a device setting and the device reads its value from column C, which is empty here. Column B is skipped, so this row may set the value to 0.",
                $"Put the value in column C: \"{b.Output},,{b.Function}\"."));
            return;
        }
        issues.Add(new Issue(Severity.Warning, $"C{b.Row}",
            $"\"{b.Output}\" is a device setting but no value follows it, so the device sets it to 0.",
            "Put the value in column C."));
    }

    // A Preferences sheet (or a standalone prefs.csv) holds "name,value" rows:
    // the preference name in column A and its value in column B (Fred Davison,
    // 2026-07-08). This is the opposite of a mode-sheet preference override,
    // where column B is skipped and the value lives in column C. Column C+ on a
    // Preferences sheet is the human Units/Description annotation, not data.
    static void ValidatePreferencesSheet(ModeSheet sheet, List<Issue> issues)
    {
        // Whole-number values seen on this sheet, so the pair-order checks
        // below can compare two rows. A repeated name keeps the last row, the
        // way the device's own sequential read does.
        var numbers = new Dictionary<string, (int Value, int Row)>(StringComparer.Ordinal);

        foreach (var b in sheet.Bindings)
        {
            if (b.Output.Length == 0) continue; // blank name sets nothing
            var value = b.Function; // column B is the value here
            var valueInC = b.InputCols.Count > 0 && b.InputCols[0] == 2 ? b.Inputs[0] : null;

            // A name the catalog has never heard of is a Warning and never an
            // error: firmware newer than this app has preferences this app
            // cannot know, and the row must survive untouched either way.
            PreferenceDefinition? def = PreferenceCatalog.TryGet(b.Output, out var found) ? found : null;
            if (def is null)
                issues.Add(new Issue(Severity.Warning, $"A{b.Row}",
                    $"\"{b.Output}\" is not a preference this app knows. It is written back exactly as it is, in case your device understands it.",
                    "Check the spelling against the preferences your device already has."));

            if (value.Length == 0)
            {
                if (valueInC != null)
                    issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                        $"On a Preferences sheet the device reads \"{b.Output}\"'s value from column B, but B is empty and the value sits in column C. (A mode sheet uses column C; a Preferences sheet uses column B.)",
                        $"Move the value into column B: \"{b.Output},{valueInC}\"."));
                else
                    issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                        $"\"{b.Output}\" has no value in column B, so the device reads it as 0.",
                        "Put the preference's value in column B."));
                continue;
            }

            // A value that isn't a number at all is an Error, matching the
            // mode-sheet override path and ValidateFunction: the device's atoi
            // reads it as 0, so the preference is simply wrong. (A number in the
            // wrong form would be a Warning, but there's no such form here.)
            var rejected = false;
            if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
                               System.Globalization.CultureInfo.InvariantCulture, out _)
                && !IsWordValuedPreference(b.Output))
            {
                issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                    $"\"{value}\" in column B is the value of \"{b.Output}\" but is not a whole number.",
                    "Most preferences take a whole number, e.g. \"50\"."));
                rejected = true;
            }

            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out var small))
                numbers[b.Output] = (small, b.Row);

            // On a Preferences sheet the value is in column B, so that is the
            // cell the catalog checks point at.
            if (def is not null) ValidateAgainstCatalog(def, value, $"B{b.Row}", rejected, issues);
        }

        ValidatePreferenceOrder(numbers, issues);
    }

    // What the catalog can prove about one value. Bounds come from the sliders
    // in the official manager, which are recommendations rather than device
    // limits, so being outside them is only a Warning. A toggle is a number to
    // the firmware, so an odd value is coerced, not misread, and that is a
    // Warning too. A choice is the one blocking case: its words come from the
    // firmware's own keyword table, and a word outside it selects the wrong
    // mode on the device instead of failing.
    static void ValidateAgainstCatalog(
        PreferenceDefinition def, string value, string cell, bool alreadyRejected, List<Issue> issues)
    {
        switch (def.Editor)
        {
            case PreferenceEditor.Integer:
                if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
                                   System.Globalization.CultureInfo.InvariantCulture, out var n))
                    return; // not a number: the whole-number error above covers it
                if (def.Minimum is int min && n < min)
                    issues.Add(new Issue(Severity.Warning, cell,
                        $"\"{value}\" is below {min}, the lowest value the official manager offers for \"{def.Name}\". The device still takes it, but it is outside the tested range.",
                        $"Use {min} or more."));
                else if (def.Maximum is int max && n > max)
                    issues.Add(new Issue(Severity.Warning, cell,
                        $"\"{value}\" is above {max}, the highest value the official manager offers for \"{def.Name}\". The device still takes it, but it is outside the tested range.",
                        $"Use {max} or less."));
                return;

            case PreferenceEditor.Toggle:
                if (alreadyRejected || value == "0" || value == "1") return;
                // A toggle is read as a number, so a stray whole number is
                // coerced rather than misread. The file still installs.
                issues.Add(new Issue(Severity.Warning, cell,
                    $"\"{value}\" is not an on/off value for \"{def.Name}\". The device reads it as a number, so anything other than 0 counts as on.",
                    "Use 1 for on or 0 for off."));
                return;

            case PreferenceEditor.Choice:
                if (alreadyRejected || def.Options.Contains(value, StringComparer.Ordinal)) return;
                issues.Add(new Issue(Severity.Error, cell,
                    $"\"{value}\" is not one of the values \"{def.Name}\" accepts.",
                    $"Use one of: {string.Join(", ", def.Options)}."));
                return;

            default: // Text: nothing is proven about its range, so nothing is claimed
                return;
        }
    }

    // The four orderings the sources actually establish, as plain pairs. There
    // is no rule language behind this on purpose: a preference file is not a
    // program, and an invented constraint can cost a disabled user the input
    // they rely on.
    static readonly (string Lower, string Upper, int Gap)[] OrderedPairs =
    {
        ("sip_puff_threshold_soft", "sip_puff_threshold", 2),
        ("sip_puff_threshold", "sip_puff_maximum", 2),
        ("lip_position_minimum", "lip_position_maximum", 5),
        ("joystick_D_Pad_inner", "joystick_D_Pad_outer", 2),
    };

    // Only runs when both preferences are present on the sheet and both are
    // whole numbers. One of a pair on its own says nothing: the other value
    // lives on the device, where this app cannot see it.
    static void ValidatePreferenceOrder(Dictionary<string, (int Value, int Row)> numbers, List<Issue> issues)
    {
        foreach (var (lower, upper, gap) in OrderedPairs)
        {
            if (!numbers.TryGetValue(lower, out var lo)) continue;
            if (!numbers.TryGetValue(upper, out var hi)) continue;
            if ((long)lo.Value + gap <= hi.Value) continue;
            issues.Add(new Issue(Severity.Warning, $"B{hi.Row}",
                $"\"{upper}\" is {hi.Value} and \"{lower}\" is {lo.Value}. The two need at least {gap} between them, or the settings run into each other.",
                $"Raise \"{upper}\" to {(long)lo.Value + gap} or more, or lower \"{lower}\"."));
        }
    }

    // bluetooth_device_mode, bluetooth_connection_mode and
    // bluetooth_remote_address take word values on the device.
    static bool IsWordValuedPreference(string name) =>
        name.StartsWith("bluetooth_", StringComparison.Ordinal);

    static void ValidateFileName(ProfileDocument doc, List<Issue> issues)
    {
        var cell = $"A{doc.FileNameCellRow}";
        var name = doc.CsvFileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new Issue(Severity.Error, cell,
                "The cell under the first sheet's keyword must contain the CSV filename.",
                "Set it to a name like \"mygame.csv\"."));
            return;
        }
        if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            || name.Length <= 4
            || name.AsSpan().ContainsAny(InvalidFileNameChars))
        {
            issues.Add(new Issue(Severity.Error, cell,
                $"\"{name}\" is not a valid configuration filename.",
                "Use the form \"something.csv\" with no spaces or special characters."));
        }
        if (string.Equals(name, "prefs.csv", StringComparison.OrdinalIgnoreCase))
            issues.Add(new Issue(Severity.Warning, cell,
                "prefs.csv is the device preferences file, not a game configuration.",
                "Use a different name unless you intend to change preferences."));
        if (doc.IsDefaultConfig)
            issues.Add(new Issue(Severity.Warning, cell,
                "This edits default.csv, the device's fallback file that is designed to stay unchanged. A wrong USB emulation value in it can disable flash-drive access, and recovery requires a physical force-erase.",
                "Prefer a new filename. The installer will ask for explicit confirmation before writing default.csv."));
    }

    static void ValidateChannel(ModeSheet sheet, List<Issue> issues)
    {
        if (sheet.Channel.Length > 0 && !Vocab.Channels.Contains(sheet.Channel))
            issues.Add(new Issue(Severity.Warning, $"C{sheet.StartRow + 2}",
                $"The device does not match \"{sheet.Channel}\" as a channel, so this mode connects over USB instead.",
                "Use \"usb\", \"bluetooth\", or \"none\", in lower case."));
    }

    static void ValidateOutput(Binding b, List<Issue> issues)
    {
        if (b.Output.Length == 0)
        {
            // A blank column A does nothing on the device either way, so this
            // is a warning: the community sheets ship a thousand pre-filled
            // rows and every unused one lands here. A row set to one of the
            // profile's own names has already been told what it does, so
            // pointing at the output cell would read as the app losing the
            // pick. Point at the name instead.
            issues.Add(b.ActionName.Length > 0
                ? new Issue(Severity.Warning, $"A{b.Row}",
                    $"\"{b.ActionName}\" has no button behind it yet, so this row does nothing on the QuadStick.",
                    $"Open Custom output names and pick the button \"{b.ActionName}\" stands for.")
                : new Issue(Severity.Warning, $"A{b.Row}",
                    "This row has no output name. The device skips it and both official converters delete it, so the row does nothing.",
                    "Pick the game button or action this row controls, e.g. \"x\" or \"left_trigger\"."));
            return;
        }
        if (Vocab.IsKnownOutput(b.Output)) return;
        // The device's own table still has these, so the row works. Saying
        // "not documented, pick another" would send someone to change a name
        // their QuadStick already answers to.
        issues.Add(Vocab.LegacyOutputs.Contains(b.Output)
            ? new Issue(Severity.Warning, $"A{b.Row}",
                $"\"{b.Output}\" is a legacy output name: the firmware knows it but the current official list does not include it.",
                "It should still work; prefer a current name if one exists, e.g. \"gyroscope_z_cw\".")
            : new Issue(Severity.Warning, $"A{b.Row}",
                $"\"{b.Output}\" is not a documented output name (PlayStation or XBox convention).",
                "Pick an output from the editor's list, e.g. \"x\", \"left_trigger\", or \"mouse_up\"."));
    }

    static void ValidateFunction(Binding b, List<Issue> issues)
    {
        var parts = b.Function.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            // Firmware: an empty (or unrecognized) function cell falls back
            // to code 0, which is "normal". Legal, just implicit.
            issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                "No output function; the device treats a blank as \"normal\".",
                "Set it to \"normal\" to make that explicit."));
            return;
        }
        if (!Vocab.FunctionArity.TryGetValue(parts[0], out var arity))
        {
            // search_for_keyword_with_parameter matches on the LENGTH of each
            // table entry, so a cell only has to START with a keyword: the
            // device reads "toggled" as toggle, not as normal. Only a cell that
            // matches no entry at all falls back to code 0, "normal".
            var prefix = Vocab.FunctionsInFirmwareOrder
                .FirstOrDefault(f => b.Function.StartsWith(f, StringComparison.Ordinal));
            issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                prefix is null
                    ? $"\"{parts[0]}\" is not a documented output function, so the device falls back to \"normal\" for this row."
                    : $"\"{parts[0]}\" is not a documented output function. It starts with \"{prefix}\", and the device stops matching there, so this row acts as \"{prefix}\".",
                $"Use one of: {string.Join(", ", Vocab.FunctionArity.Keys)}."));
            return;
        }
        // Every parameter is optional per the user manual: "tap 500",
        // "repeat 4", and "delay_on 500 1" are all legal community usage.
        // Only MORE than the documented maximum is an error.
        var args = parts.Skip(1).ToArray();
        if (args.Length > arity.Max)
            issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                $"\"{parts[0]}\" takes at most {arity.Max} parameter(s), found {args.Length}. The device reads the ones it knows and drops the rest.",
                "Remove the extra values."));
        // The device converts parameters with atoi: whole, non-negative
        // integers. The first parameter is stored in 14 bits (max 16383).
        // A decimal or negative value doesn't fail on the device, it just
        // silently becomes something else, so those are warnings.
        for (int i = 0; i < args.Length; i++)
        {
            if (!long.TryParse(args[i], System.Globalization.NumberStyles.Integer,
                               System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (double.TryParse(args[i], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out _))
                    issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                        $"\"{args[i]}\" has a decimal part. The device reads whole numbers only, so it acts as \"{args[i].Split('.')[0]}\".",
                        "Use a whole number."));
                else
                    // atoi on a word gives 0. Wrong timing, not a broken file.
                    issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                        $"\"{args[i]}\" is not a number. Parameters to \"{parts[0]}\" must be whole numbers, and the device reads this one as 0.",
                        "Replace it with a whole number, e.g. \"repeat 4\"."));
            }
            else if (n < 0)
                issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                    $"\"{args[i]}\" is negative; the device does not handle negative parameters predictably.",
                    "Use a value of 0 or more."));
            else if (i == 0 && n > 16383)
                issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                    $"\"{args[i]}\" is larger than 16383, the device's limit for the first parameter; it overflows into the second parameter.",
                    "Use a value up to 16383."));
        }
    }

    static void ValidateInputs(Binding b, List<Issue> issues)
    {
        // A row with an output and no input never fires, but it is NOT flagged
        // here. The factory template ships twelve of them on purpose (dpad_N
        // through dpad_NW and the right stick, all "output,normal," waiting for
        // the user to choose an input), so a file-level warning would open every
        // new profile with twelve complaints. A row left inputless by an edit is
        // byte-identical to those placeholders, so the file cannot tell them
        // apart and neither can this function. The edit knows, so the advanced
        // grid says it there instead: see ImportReviewWindow.Consequence.

        // Point the issue at the input's REAL column (C..J), not always C, so
        // Fix First and the cell highlight land on the offending input instead
        // of the first one when the bad token sits in a later column.
        for (int i = 0; i < b.Inputs.Count; i++)
        {
            var input = b.Inputs[i];
            if (Vocab.Inputs.Contains(input)) continue;
            if (input == Vocab.NoneInput) continue; // real device keyword, same as blank
            int col = i < b.InputCols.Count ? b.InputCols[i] : 2;
            if (Vocab.LegacyInputs.Contains(input))
                issues.Add(new Issue(Severity.Warning, $"{(char)('A' + col)}{b.Row}",
                    $"\"{input}\" is a legacy input name: the firmware knows it but the current official list does not include it.",
                    "It should still work; prefer a current name if one exists."));
            else
                // Usually a note somebody typed beside a binding ("Aim",
                // "Comments"). The device does not match the keyword and moves
                // on, so the binding still works and the file still installs.
                issues.Add(new Issue(Severity.Warning, $"{(char)('A' + col)}{b.Row}",
                    $"\"{input}\" is not a documented input name, so the device ignores it.",
                    "Pick an input from the Inputs dropdown list, e.g. \"mp_left_sip\" or \"lip\", or move the text to the notes column.",
                    IssueKind.UnknownInput));
        }
    }
}
