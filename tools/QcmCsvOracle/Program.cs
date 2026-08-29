using System.Text;
using System.Text.Json;
using QuadStick.Format;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: QcmCsvOracle <manifest.json> [output-dir]");
    return 2;
}

var root = FindRepoRoot();
var manifestPath = Path.GetFullPath(args[0]);
var outputDir = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(root, "fixtures", "oracle");
Directory.CreateDirectory(outputDir);

using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var count = 0;
foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
{
    if (fixture.GetProperty("kind").GetString() != "csv-edge") continue;
    var id = fixture.GetProperty("id").GetString() ?? throw new InvalidDataException("fixture id missing");
    var relative = fixture.GetProperty("path").GetString() ?? throw new InvalidDataException($"{id}: fixture path missing");
    var path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException($"{id}: fixture path escapes repository");

    var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
    var rows = Csv.Parse(text);
    var sb = new StringBuilder();
    sb.AppendLine("qcm-csv-parity-1");
    sb.Append("rows=").Append(rows.Count).Append('\n');
    foreach (var row in rows)
    {
        sb.AppendLine("row");
        foreach (var cell in row)
            sb.Append("cell=").Append(Hex(Encoding.UTF8.GetBytes(cell))).Append('\n');
        sb.AppendLine("endrow");
    }
    sb.Append("write=").Append(Hex(Encoding.UTF8.GetBytes(Csv.Write(rows)))).Append('\n');
    File.WriteAllText(Path.Combine(outputDir, id + ".csv-parity.txt"), sb.ToString(), new UTF8Encoding(false));
    count++;
}

Console.WriteLine($"generated C# CSV parity artifacts for {count} fixtures");
return 0;

static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "QuadStick.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Run QcmCsvOracle from inside the repository.");
}
