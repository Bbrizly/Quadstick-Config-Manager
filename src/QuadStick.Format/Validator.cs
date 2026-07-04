namespace QuadStick.Format;

// Checks a parsed profile against the format rules. Errors block install.
public static class Validator
{
    public static List<Issue> Validate(ProfileDocument doc)
    {
        var issues = new List<Issue>();
        ValidateFileName(doc, issues);

        foreach (var sheet in doc.Sheets)
        {
            // Preferences and Infrared sheets carry name,value rows, not
            // output/function/input bindings. Binding vocabulary does not
            // apply to them; validating it would produce false errors on
            // every profile with a Preferences tab.
            if (sheet.Type != SheetType.ProfileName) continue;

            ValidateChannel(sheet, issues);
            foreach (var b in sheet.Bindings)
            {
                ValidateOutput(b, issues);
                ValidateFunction(b, issues);
                ValidateInputs(b, issues);
            }
        }
        return issues;
    }

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
            || name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' }) >= 0)
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
                "This row has no output name, so the device stops reading the sheet here.",
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
            issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                "Missing output function.",
                "Set the function, e.g. \"normal\"."));
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
        foreach (var a in args)
            if (!double.TryParse(a, out _))
                issues.Add(new Issue(Severity.Error, $"B{b.Row}",
                    $"\"{a}\" is not a number. Parameters to \"{parts[0]}\" must be numeric.",
                    "Replace it with a number, e.g. \"repeat 4\"."));
    }

    static void ValidateInputs(Binding b, List<Issue> issues)
    {
        foreach (var input in b.Inputs)
            if (!Vocab.Inputs.Contains(input))
                issues.Add(new Issue(Severity.Error, $"C{b.Row}",
                    $"\"{input}\" is not a documented input name.",
                    "Pick an input from the Inputs dropdown list, e.g. \"mp_left_sip\" or \"lip\"."));
    }
}
