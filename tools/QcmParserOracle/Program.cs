using System.Text;
using System.Text.Json;
using QuadStick.Format;

if (args.Length != 2)
    throw new InvalidOperationException("Usage: qcm-parser-oracle <manifest> <output-dir>");

const string schemaVersion = "qcm-parity-1";
const string legacyBase = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

var root = FindRepoRoot();
var manifestPath = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDir);

using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
{
    if (fixture.GetProperty("kind").GetString() != "profile-csv") continue;

    var id = fixture.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Fixture id is missing.");
    var relativePath = fixture.GetProperty("path").GetString()
        ?? throw new InvalidOperationException($"Fixture '{id}' path is missing.");
    var text = File.ReadAllText(Path.Combine(root, relativePath), Encoding.UTF8);
    var (doc, _) = Parser.Parse(text);

    var result = new
    {
        schemaVersion,
        legacyBase,
        fixtureId = id,
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
            sheets = doc.Sheets.Select(sheet => new
            {
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
    };

    var rendered = JsonSerializer.Serialize(result, OracleJson.Options) + "\n";
    File.WriteAllText(
        Path.Combine(outputDir, $"{id}.parser-structure.txt"),
        rendered,
        new UTF8Encoding(false));
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "QuadStick.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Run qcm-parser-oracle from inside the repository.");
}


// One cached options instance: CA1869, and the oracle must serialize identically every run.
static class OracleJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
