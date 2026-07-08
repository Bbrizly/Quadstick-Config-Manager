namespace QuadStick.Format;

// Checks a parsed profile against the format rules. Errors block install.
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
            if (sheet.Bindings.Count > 128)
                issues.Add(new Issue(Severity.Warning, $"A{sheet.Bindings[128].Row}",
                    $"This mode has {sheet.Bindings.Count} rows; the device reads the first 128 and ignores the rest.",
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
    // With increment_value/decrement_value it's instead a live binding that
    // adjusts the setting from an input, so it validates as a binding.
    static bool IsPreferenceOverride(Binding b) =>
        Vocab.PreferenceOverrides.Contains(b.Output)
        && !b.Function.StartsWith("increment_value", StringComparison.Ordinal)
        && !b.Function.StartsWith("decrement_value", StringComparison.Ordinal);

    static void ValidatePreferenceOverride(Binding b, List<Issue> issues)
    {
        // The firmware reads the VALUE from the third column (it skips the
        // function column). Files in the wild also carry the value in column
        // B; firmware 1476 would read those as 0, so flag them.
        var valueInC = b.Inputs.Count > 0 && b.InputCols.Count > 0 && b.InputCols[0] == 2
            ? b.Inputs[0] : null;
        if (valueInC != null)
        {
            if (!long.TryParse(valueInC, System.Globalization.NumberStyles.Integer,
                               System.Globalization.CultureInfo.InvariantCulture, out _)
                && !IsWordValuedPreference(b.Output))
                issues.Add(new Issue(Severity.Error, $"C{b.Row}",
                    $"\"{valueInC}\" is not a whole number. \"{b.Output}\" is a device setting; this cell is its value.",
                    "Replace it with a whole number, e.g. \"50\"."));
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
        foreach (var b in sheet.Bindings)
        {
            if (b.Output.Length == 0) continue; // blank name sets nothing
            var value = b.Function; // column B is the value here
            var valueInC = b.InputCols.Count > 0 && b.InputCols[0] == 2 ? b.Inputs[0] : null;

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

            if (!long.TryParse(value, System.Globalization.NumberStyles.Integer,
                               System.Globalization.CultureInfo.InvariantCulture, out _)
                && !IsWordValuedPreference(b.Output))
                issues.Add(new Issue(Severity.Warning, $"B{b.Row}",
                    $"\"{value}\" in column B is the value of \"{b.Output}\" but is not a whole number.",
                    "Most preferences take a whole number, e.g. \"50\"."));
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
                $"\"{sheet.Channel}\" is not a recognized channel.",
                "Expected \"usb\" or \"bluetooth\"."));
    }

    static void ValidateOutput(Binding b, List<Issue> issues)
    {
        if (b.Output.Length == 0)
        {
            issues.Add(new Issue(Severity.Error, $"A{b.Row}",
                "This row has no output name. The device skips it and both official converters delete it, so the row does nothing.",
                "Pick the game button or action this row controls, e.g. \"x\" or \"left_trigger\"."));
            return;
        }
        if (!Vocab.IsKnownOutput(b.Output))
            issues.Add(new Issue(Severity.Warning, $"A{b.Row}",
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
            issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                $"\"{parts[0]}\" is not a documented output function.",
                $"Use one of: {string.Join(", ", Vocab.FunctionArity.Keys)}."));
            return;
        }
        // Every parameter is optional per the user manual: "tap 500",
        // "repeat 4", and "delay_on 500 1" are all legal community usage.
        // Only MORE than the documented maximum is an error.
        var args = parts.Skip(1).ToArray();
        if (args.Length > arity.Max)
            issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                $"\"{parts[0]}\" takes at most {arity.Max} parameter(s), found {args.Length}.",
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
                    issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                        $"\"{args[i]}\" is not a number. Parameters to \"{parts[0]}\" must be whole numbers.",
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
                issues.Add(new Issue(Severity.Error, $"{(char)('A' + col)}{b.Row}",
                    $"\"{input}\" is not a documented input name.",
                    "Pick an input from the Inputs dropdown list, e.g. \"mp_left_sip\" or \"lip\"."));
        }
    }
}
