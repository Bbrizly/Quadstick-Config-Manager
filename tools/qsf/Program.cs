using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuadStick.Format;

// qsf: the profile format as JSON in and JSON out, so an agent can read and
// write real QuadStick profiles without a hand-rolled second parser. Every
// write goes through an explicit op that names its cell, and an op naming a
// token the device does not know is refused rather than written.

const int FirstInputCol = 2;   // column C
const int MaxInputs = 8;       // columns C..J

var opts = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

if (args.Length == 0) { Usage(); return 2; }

try
{
    return args[0] switch
    {
        "inspect" => Inspect(args[1..]),
        "vocab" => Vocab_(),
        "validate" => Validate(args[1..]),
        "apply" => Apply(args[1..]),
        "diff" => Diff(args[1..]),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Emit(new { ok = false, error = ex.Message });
    return 2;
}

void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, opts));

int Usage()
{
    Console.Error.WriteLine("""
        qsf inspect <file.csv>...            structured read of each profile
        qsf vocab                            every input, output and function the device knows
        qsf validate <file.csv>              parse + validate, exit 1 if any error
        qsf apply --from <file.csv>|--template <name.csv> --ops <file.json>|- --out <file.csv>
        qsf diff <a.csv> <b.csv>             cell level changes
        """);
    return 2;
}

// ---- read ----------------------------------------------------------------

int Inspect(string[] paths)
{
    if (paths.Length == 0) return Usage();
    Emit(new { profiles = paths.Select(p => Describe(p, Open(File.ReadAllText(p), out int n), n)).ToArray() });
    return 0;
}

// Every row number qsf reports or accepts is a line number in the file AS THE
// DEVICE READS IT, which is not always the file on disk: a profile with no
// version header, or a mode with no blank line above it, gains rows on the way
// out and every row below moves. Normalising here instead of at write time is
// what stops an op aimed at row 7 landing on row 8.
ProfileFile Open(string csvText, out int rowsInserted)
{
    var pf = ProfileFile.Load(csvText);
    int before = pf.Grid.Count;
    pf.NormalizeForDeviceCsv();
    rowsInserted = pf.Grid.Count - before;
    return pf;
}

object Describe(string path, ProfileFile pf, int rowsInserted)
{
    var d = pf.Document;
    return new
    {
        path,
        // Non-zero means the rows below moved to make the file readable by the
        // device, so these row numbers are not the disk file's line numbers.
        rowsInsertedForDevice = rowsInserted,
        title = d.Title,
        csvFileName = d.CsvFileName,
        headerVersion = d.HasVersionHeader ? d.HeaderVersion : null,
        modes = d.Sheets.Select((s, i) => new
        {
            index = i,
            type = s.Type.ToString(),
            // The firmware counts sheets and never reads this name, so the
            // number is the identity and two modes may share a name.
            number = d.Sheets.Take(i + 1).Count(x => x.Type == SheetType.ProfileName),
            name = s.ModeName,
            channel = s.Channel,
            label = s.HeaderLabel,
            startRow = s.StartRow,
            bindings = s.Bindings.Select(b => new
            {
                row = b.Row,
                output = b.Output,
                function = b.Function,
                inputs = b.Inputs,
                action = b.ActionName.Length > 0 ? b.ActionName : null,
            }).ToArray(),
        }).ToArray(),
        issues = pf.Issues.Select(IssueOut).ToArray(),
    };
}

object IssueOut(Issue i) => new
{
    severity = i.Severity.ToString().ToLowerInvariant(),
    cell = i.Cell,
    message = i.Message,
    fix = i.Fix,
    kind = i.Kind.ToString(),
};

int Vocab_()
{
    Emit(new
    {
        inputs = Vocab.Inputs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        outputs = new
        {
            ps3 = Vocab.OutputsPs3.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            xbox = Vocab.OutputsXbox.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        },
        preferenceOverrides = Vocab.PreferenceOverrides.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
        functions = Vocab.FunctionsInFirmwareOrder.Select(f => new
        {
            name = f,
            minParams = Vocab.FunctionArity[f].Min,
            maxParams = Vocab.FunctionArity[f].Max,
        }).ToArray(),
        legacyInputs = Vocab.LegacyInputs.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
    });
    return 0;
}

int Validate(string[] paths)
{
    if (paths.Length == 0) return Usage();
    var pf = Open(File.ReadAllText(paths[0]), out int inserted);
    int errors = pf.Issues.Count(i => i.Severity == Severity.Error);
    Emit(new
    {
        path = paths[0],
        rowsInsertedForDevice = inserted,
        ok = errors == 0,
        errors,
        warnings = pf.Issues.Count(i => i.Severity == Severity.Warning),
        issues = pf.Issues.Select(IssueOut).ToArray(),
    });
    return errors == 0 ? 0 : 1;
}

// ---- write ---------------------------------------------------------------

int Apply(string[] a)
{
    string? from = null, template = null, opsPath = null, outPath = null;
    for (int i = 0; i < a.Length - 1; i++)
        switch (a[i])
        {
            case "--from": from = a[++i]; break;
            case "--template": template = a[++i]; break;
            case "--ops": opsPath = a[++i]; break;
            case "--out": outPath = a[++i]; break;
        }
    if (opsPath is null || outPath is null || (from is null && template is null)) return Usage();

    var pf = from is not null
        ? Open(File.ReadAllText(from), out _)
        : Open(ProfileFile.NewFromTemplate(template!).ToCsvText(), out _);

    var json = opsPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(opsPath);
    var ops = JsonNode.Parse(json)?.AsArray() ?? throw new InvalidOperationException("ops must be a JSON array");

    var applied = new List<object>();
    var rejected = new List<object>();

    for (int i = 0; i < ops.Count; i++)
    {
        var op = ops[i]!.AsObject();
        string kind = Str(op, "op");
        string? why = op.ContainsKey("why") ? Str(op, "why") : null;
        void Ok(object? detail = null) => applied.Add(new { index = i, op = kind, why, detail });
        void No(string reason) => rejected.Add(new { index = i, op = kind, why, reason });

        switch (kind)
        {
            case "set_filename":
                pf.SetCell(pf.Document.FileNameCellRow, 0, Str(op, "name"));
                Ok();
                break;

            case "add_mode":
            {
                int mode = pf.AddModeSheet(Str(op, "name"));
                // A new sheet lands without the blank line the device needs
                // above it. Settle that now, so the row numbers the next op
                // uses are the ones the file will actually be written with.
                pf.NormalizeForDeviceCsv();
                Ok(new { mode });
                break;
            }

            case "rename_mode":
                if (pf.RenameMode(Int(op, "mode"), Str(op, "name"))) Ok();
                else No("mode index out of range, or the name is unchanged");
                break;

            case "add_row":
            {
                int m = Int(op, "mode");
                if (m < 0 || m >= pf.Document.Sheets.Count) { No($"no mode at index {m}"); break; }
                Ok(new { row = pf.AddBindingRow(pf.Document.Sheets[m]) });
                break;
            }

            case "delete_row":
                pf.DeleteRow(Int(op, "row"));
                Ok();
                break;

            case "set_cell":
                pf.SetCell(Int(op, "row"), Int(op, "col"), Str(op, "value"));
                Ok();
                break;

            case "set_binding":
            {
                int row = Int(op, "row");
                string output = Str(op, "output");
                string function = op.ContainsKey("function") ? Str(op, "function") : "normal";
                var inputs = op["inputs"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray()
                             ?? Array.Empty<string>();
                string action = op.ContainsKey("action") ? Str(op, "action") : "";

                var reason = RejectBinding(pf, row, output, function, inputs, action);
                if (reason is not null) { No(reason); break; }

                pf.SetOutput(row, output, action);
                pf.SetCell(row, 1, function);
                for (int c = 0; c < MaxInputs; c++)
                    pf.SetCell(row, FirstInputCol + c, c < inputs.Length ? inputs[c] : "");
                Ok(new { cell = A1(row, 0) });
                break;
            }

            default:
                No($"unknown op '{kind}'");
                break;
        }
    }

    int shifted = 0;
    if (rejected.Count == 0)
    {
        int before = pf.Grid.Count;
        pf.NormalizeForDeviceCsv();
        shifted = pf.Grid.Count - before;
        ProfileFile.WriteAtomic(outPath, pf.ToCsvText());
    }
    int errors = pf.Issues.Count(x => x.Severity == Severity.Error);
    bool ok = rejected.Count == 0 && errors == 0;

    Emit(new
    {
        ok,
        wrote = rejected.Count == 0 ? outPath : null,
        // Should be 0. Anything else means the write moved rows out from under
        // the row numbers reported above, and they need re-reading.
        rowsInsertedAtWrite = shifted,
        applied,
        rejected,
        errors,
        warnings = pf.Issues.Count(x => x.Severity == Severity.Warning),
        issues = pf.Issues.Select(IssueOut).ToArray(),
    });
    return ok ? 0 : 1;
}

// Refuse a binding the device could not read, and say which cell is wrong.
// The point is that a wrong token never reaches the file: a profile that a
// person then loads onto a device they steer with their mouth.
string? RejectBinding(ProfileFile pf, int row, string output, string function, string[] inputs, string action)
{
    if (row < 1) return $"row {row} is not a row";

    // A sheet's first three rows are its keyword, its file name slot and its
    // column labels. A binding written over one of them reads as a binding to
    // nobody: the app still parses the file, the device quietly loses the mode.
    var sheet = pf.Document.Sheets.LastOrDefault(s => s.StartRow <= row);
    if (sheet is null)
        return $"row {row} is above the first mode, so it is not part of any sheet";
    if (row <= sheet.StartRow + 2)
        return $"row {row} is one of the '{sheet.ModeName}' sheet's header rows; add_row first, then bind the row it hands back";
    if (sheet.Type != SheetType.ProfileName)
        return $"row {row} is in a {sheet.Type} sheet, which holds settings rather than bindings";

    if (!Vocab.IsKnownOutput(output) && !Vocab.PreferenceOverrides.Contains(output))
        return $"'{output}' is not an output the device knows (case sensitive). See qsf vocab.";

    var parts = function.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return "function is empty; 'normal' is the plain one";
    if (!Vocab.FunctionArity.TryGetValue(parts[0], out var arity))
        return $"'{parts[0]}' is not a function the device knows. See qsf vocab.";
    if (parts.Length - 1 > arity.Max)
        return $"'{parts[0]}' takes at most {arity.Max} parameter(s), got {parts.Length - 1}";

    if (inputs.Length > MaxInputs) return $"a row holds at most {MaxInputs} inputs, got {inputs.Length}";
    foreach (var input in inputs)
        if (input != Vocab.NoneInput && !Vocab.Inputs.Contains(input))
            return $"'{input}' is not an input the device knows (case sensitive). See qsf vocab.";

    if (action.Length == 0) return null;
    if (!ProfileFile.IsLegalActionName(action))
        return $"'{action}' cannot be an action name: 1 to {ProfileFile.MaxActionName} characters, and not the name of an output";
    var clash = pf.ActionTokens().FirstOrDefault(kv =>
        string.Equals(kv.Key, action, StringComparison.OrdinalIgnoreCase) && kv.Value != output);
    return clash.Key is not null
        ? $"'{action}' already means '{clash.Value}' in this profile; one name, one output"
        : null;
}

int Diff(string[] a)
{
    if (a.Length < 2) return Usage();
    var x = ProfileFile.Load(File.ReadAllText(a[0])).Grid;
    var y = ProfileFile.Load(File.ReadAllText(a[1])).Grid;
    var changes = new List<object>();
    for (int r = 0; r < Math.Max(x.Count, y.Count); r++)
    {
        var rx = r < x.Count ? x[r] : Array.Empty<string>();
        var ry = r < y.Count ? y[r] : Array.Empty<string>();
        for (int c = 0; c < Math.Max(rx.Length, ry.Length); c++)
        {
            string from = c < rx.Length ? rx[c].Trim() : "";
            string to = c < ry.Length ? ry[c].Trim() : "";
            if (from != to) changes.Add(new { cell = A1(r + 1, c), row = r + 1, col = c, from, to });
        }
    }
    Emit(new { a = a[0], b = a[1], changes });
    return 0;
}

static string A1(int row, int col)
{
    var name = "";
    for (int n = col; ; n = n / 26 - 1)
    {
        name = (char)('A' + n % 26) + name;
        if (n < 26) break;
    }
    return name + row;
}

static string Str(JsonObject o, string key) =>
    o[key]?.GetValue<string>() ?? throw new InvalidOperationException($"op is missing '{key}'");

static int Int(JsonObject o, string key) =>
    o[key]?.GetValue<int>() ?? throw new InvalidOperationException($"op is missing '{key}'");
