using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuadStick.Format;

namespace QuadStick.Oracle;

internal static class Program
{
    const string SchemaVersion = "qcm-parity-1";
    const string LegacyBase = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        try
        {
            if (args.Length == 0) return Usage();
            return args[0] switch
            {
                "inspect-canonical" => One(args, normalize: false),
                "normalize-canonical" => One(args, normalize: true),
                "apply-canonical" => Apply(args),
                "firmware-canonical" => Firmware(args),
                "generate" => Generate(args),
                "selfcheck" => SelfCheck(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("""
            qcm-oracle inspect-canonical <profile.csv|profile.xlsx>
            qcm-oracle normalize-canonical <profile.csv|profile.xlsx>
            qcm-oracle apply-canonical <profile.csv|profile.xlsx> <ops.json>
            qcm-oracle firmware-canonical <profile.csv|profile.xlsx>
            qcm-oracle generate [manifest.json] [output-dir]
            qcm-oracle selfcheck
            """);
        return 2;
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuadStick.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Run qcm-oracle from inside the repository.");
    }

    static int One(string[] args, bool normalize)
    {
        if (args.Length != 2) return Usage();
        var loaded = Load(args[1]);
        if (normalize) loaded.Profile.NormalizeForDeviceCsv();
        Emit(Canonical(normalize ? "normalize-canonical" : "inspect-canonical", loaded));
        return 0;
    }

    static int Firmware(string[] args)
    {
        if (args.Length != 2) return Usage();
        var loaded = Load(args[1]);
        var text = loaded.Profile.ToCsvText();
        Emit(new
        {
            schemaVersion = SchemaVersion,
            legacyBase = LegacyBase,
            command = "firmware-canonical",
            source = Source(loaded),
            modes = FirmwareOracle.Read(text).Select((m, i) => new
            {
                index = i,
                channel = m.Channel,
                bindings = m.Bindings.Select(b => new { b.Output, b.Function, b.Inputs }).ToArray(),
            }).ToArray(),
            serialized = Serialized(text),
        });
        return 0;
    }

    static int Apply(string[] args)
    {
        if (args.Length != 3) return Usage();
        var loaded = Load(args[1]);
        using var opsDoc = JsonDocument.Parse(File.ReadAllBytes(args[2]));
        if (opsDoc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ops root must be an array");

        var results = new List<object>();
        int index = 0;
        foreach (var op in opsDoc.RootElement.EnumerateArray())
        {
            string kind = RequiredString(op, "op");
            bool applied = kind switch
            {
                "set_cell" => SetCell(loaded.Profile, op),
                "set_output" => loaded.Profile.SetOutput(RequiredInt(op, "row"), RequiredString(op, "token"), OptionalString(op, "action") ?? ""),
                "add_row" => AddRow(loaded.Profile, op),
                "delete_row" => DeleteRow(loaded.Profile, op),
                "move_row" => MoveRow(loaded.Profile, op),
                "add_mode" => loaded.Profile.AddModeSheet(RequiredString(op, "name")) >= 0,
                "rename_mode" => loaded.Profile.RenameMode(RequiredInt(op, "sheet"), RequiredString(op, "name")),
                "set_mode_channel" => loaded.Profile.SetModeChannel(RequiredInt(op, "sheet"), RequiredString(op, "channel")),
                "normalize" => Normalize(loaded.Profile),
                "undo" => loaded.Profile.Undo(),
                _ => throw new InvalidDataException($"unknown oracle op '{kind}'"),
            };
            results.Add(new { index, op = kind, applied });
            index++;
        }

        Emit(new
        {
            schemaVersion = SchemaVersion,
            legacyBase = LegacyBase,
            command = "apply-canonical",
            source = Source(loaded),
            operations = results,
            snapshot = Snapshot(loaded.Profile),
        });
        return 0;
    }

    static bool SetCell(ProfileFile profile, JsonElement op)
    {
        profile.SetCell(RequiredInt(op, "row"), RequiredInt(op, "col"), RequiredString(op, "value"));
        return true;
    }

    static bool AddRow(ProfileFile profile, JsonElement op)
    {
        int sheet = RequiredInt(op, "sheet");
        if (sheet < 0 || sheet >= profile.Document.Sheets.Count) return false;
        profile.AddBindingRow(profile.Document.Sheets[sheet]);
        return true;
    }

    static bool DeleteRow(ProfileFile profile, JsonElement op)
    {
        int row = RequiredInt(op, "row");
        if (row < 1 || row > profile.Grid.Count) return false;
        profile.DeleteRow(row);
        return true;
    }

    static bool MoveRow(ProfileFile profile, JsonElement op)
    {
        int from = RequiredInt(op, "from");
        int to = RequiredInt(op, "to");
        if (from < 1 || from > profile.Grid.Count || to < 1 || to > profile.Grid.Count || from == to) return false;
        profile.MoveRow(from, to);
        return true;
    }

    static bool Normalize(ProfileFile profile)
    {
        var before = profile.Grid.Select(r => string.Join("\u001f", r)).ToArray();
        profile.NormalizeForDeviceCsv();
        var after = profile.Grid.Select(r => string.Join("\u001f", r)).ToArray();
        return !before.SequenceEqual(after, StringComparer.Ordinal);
    }

    static int Generate(string[] args)
    {
        var root = FindRepoRoot();
        string manifestPath = args.Length >= 2 ? args[1] : Path.Combine(root, "fixtures", "manifest.json");
        string outDir = args.Length >= 3 ? args[2] : Path.Combine(root, "fixtures", "oracle");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Directory.CreateDirectory(outDir);
        int written = 0;
        foreach (var fixture in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            string kind = RequiredString(fixture, "kind");
            if (kind is not ("profile-csv" or "csv-edge" or "xlsx")) continue;
            string id = RequiredString(fixture, "id");
            string path = Path.Combine(root, RequiredString(fixture, "path"));
            var loaded = Load(path);
            File.WriteAllText(Path.Combine(outDir, id + ".inspect.json"), Render(Canonical("inspect-canonical", loaded)) + "\n", new UTF8Encoding(false));
            var normalized = Load(path);
            normalized.Profile.NormalizeForDeviceCsv();
            File.WriteAllText(Path.Combine(outDir, id + ".normalize.json"), Render(Canonical("normalize-canonical", normalized)) + "\n", new UTF8Encoding(false));
            if (kind != "csv-edge")
            {
                var firmware = new
                {
                    schemaVersion = SchemaVersion,
                    legacyBase = LegacyBase,
                    command = "firmware-canonical",
                    source = Source(loaded),
                    modes = FirmwareOracle.Read(loaded.Profile.ToCsvText()).Select((m, i) => new
                    {
                        index = i,
                        channel = m.Channel,
                        bindings = m.Bindings.Select(b => new { b.Output, b.Function, b.Inputs }).ToArray(),
                    }).ToArray(),
                    serialized = Serialized(loaded.Profile.ToCsvText()),
                };
                File.WriteAllText(Path.Combine(outDir, id + ".firmware.json"), Render(firmware) + "\n", new UTF8Encoding(false));
            }
            written++;
        }
        Console.WriteLine($"generated canonical oracle outputs for {written} fixtures");
        return 0;
    }

    static int SelfCheck()
    {
        const string sample = "Profile Name,,Mode\r\nconfig.csv\r\nPlayStation Outputs,Function,usb\r\ntriangle,normal,lip\r\n";
        var a = Loaded.FromText("selfcheck.csv", Encoding.UTF8.GetBytes(sample), sample);
        var b = Loaded.FromText("selfcheck.csv", Encoding.UTF8.GetBytes(sample), sample);
        string first = Render(Canonical("inspect-canonical", a));
        string second = Render(Canonical("inspect-canonical", b));
        if (!string.Equals(first, second, StringComparison.Ordinal))
            throw new InvalidOperationException("canonical output is not deterministic");

        var normalized = Loaded.FromText("selfcheck.csv", Encoding.UTF8.GetBytes(sample), sample);
        normalized.Profile.NormalizeForDeviceCsv();
        string once = normalized.Profile.ToCsvText();
        normalized.Profile.NormalizeForDeviceCsv();
        string twice = normalized.Profile.ToCsvText();
        if (!string.Equals(once, twice, StringComparison.Ordinal))
            throw new InvalidOperationException("normalization is not idempotent");

        Console.WriteLine("qcm-oracle selfcheck OK");
        return 0;
    }

    static object Canonical(string command, Loaded loaded) => new
    {
        schemaVersion = SchemaVersion,
        legacyBase = LegacyBase,
        command,
        source = Source(loaded),
        import = loaded.Import,
        snapshot = Snapshot(loaded.Profile),
    };

    static object Snapshot(ProfileFile profile)
    {
        string serialized = profile.ToCsvText();
        var doc = profile.Document;
        return new
        {
            rawGrid = profile.Grid.Select(row => row.ToArray()).ToArray(),
            document = new
            {
                csvFileName = doc.CsvFileName,
                fileNameCellRow = doc.FileNameCellRow,
                hasVersionHeader = doc.HasVersionHeader,
                headerVersion = doc.HeaderVersion,
                headerSource = doc.HeaderSource,
                headerName = doc.HeaderName,
                title = doc.Title,
                isDefaultConfig = doc.IsDefaultConfig,
                isDevicePreferences = doc.IsDevicePreferences,
                sheets = doc.Sheets.Select((sheet, index) => new
                {
                    index,
                    type = sheet.Type.ToString(),
                    modeName = sheet.ModeName,
                    csvFileName = sheet.CsvFileName,
                    headerLabel = sheet.HeaderLabel,
                    channel = sheet.Channel,
                    startRow = sheet.StartRow,
                    bindings = sheet.Bindings.Select(binding => new
                    {
                        row = binding.Row,
                        output = binding.Output,
                        function = binding.Function,
                        inputs = binding.Inputs.ToArray(),
                        inputCols = binding.InputCols.ToArray(),
                        actionName = binding.ActionName,
                    }).ToArray(),
                }).ToArray(),
            },
            issues = profile.Issues.Select(issue => new
            {
                severity = issue.Severity.ToString(),
                cell = issue.Cell,
                kind = issue.Kind.ToString(),
                message = issue.Message,
                fix = issue.Fix,
            }).ToArray(),
            editor = new { dirty = profile.Dirty, revision = profile.Revision, canUndo = profile.CanUndo },
            serialized = Serialized(serialized),
        };
    }

    static object Source(Loaded loaded) => new
    {
        name = loaded.Name,
        sha256 = Hex(SHA256.HashData(loaded.OriginalBytes)),
        kind = loaded.Kind,
    };

    static object Serialized(string text) => new
    {
        sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
        utf8Bytes = Encoding.UTF8.GetByteCount(text),
        text,
    };

    static Loaded Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (Xlsx.LooksLikeXlsx(bytes))
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var imported = Xlsx.Import(stream);
            return new Loaded(Path.GetFileName(path), bytes, "xlsx", ProfileFile.Load(imported.Csv), new
            {
                skipped = imported.Skipped.Select(s => new { s.Name, kind = s.Kind.ToString(), rows = s.Rows.Count }).ToArray(),
                limitation = imported.Limitation,
                renamed = imported.Renamed.Select(r => new { r.ModeNumber, r.TabName, r.CellC1 }).ToArray(),
            });
        }
        var text = Encoding.UTF8.GetString(bytes);
        return Loaded.FromText(Path.GetFileName(path), bytes, text);
    }

    static string RequiredString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"missing string '{name}'");

    static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static int RequiredInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.TryGetInt32(out var n)
            ? n
            : throw new InvalidDataException($"missing integer '{name}'");

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    static string Render(object value) => JsonSerializer.Serialize(value, Json);
    static void Emit(object value) => Console.WriteLine(Render(value));

    sealed record Loaded(string Name, byte[] OriginalBytes, string Kind, ProfileFile Profile, object? Import)
    {
        internal static Loaded FromText(string name, byte[] bytes, string text) =>
            new(name, bytes, "csv", ProfileFile.Load(text), null);
    }
}
