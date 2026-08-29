using System.Globalization;
using System.Text;
using System.Text.Json;
using QuadStick.Format;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

const string schemaVersion = "qcm-parity-1";
const string legacyBase = "f7783944387202bcafaeb7ff3f67789098fa6a4e";

var root = FindRepoRoot();
var template = File.ReadAllText(
    Path.Combine(root, "src", "QuadStick.Format", "Templates", "default-template.csv"),
    Encoding.UTF8);

var result = new
{
    schemaVersion,
    legacyBase,
    command = "catalog-canonical",
    vocab = new
    {
        inputs = Sorted(Vocab.Inputs),
        outputsPs3 = Sorted(Vocab.OutputsPs3),
        outputsXbox = Sorted(Vocab.OutputsXbox),
        knownOutputs = Sorted(Vocab.KnownOutputs),
        functionsInFirmwareOrder = Vocab.FunctionsInFirmwareOrder.ToArray(),
        functionArity = Vocab.FunctionsInFirmwareOrder.Select(name => new
        {
            name,
            min = Vocab.FunctionArity[name].Min,
            max = Vocab.FunctionArity[name].Max,
        }).ToArray(),
        preferenceOverrides = Sorted(Vocab.PreferenceOverrides),
        legacyInputs = Sorted(Vocab.LegacyInputs),
        legacyOutputs = Sorted(Vocab.LegacyOutputs),
        channels = Sorted(Vocab.Channels),
    },
    preferences = PreferenceCatalog.All.Select((p, index) => new
    {
        index,
        name = p.Name,
        label = p.Label,
        category = p.Category,
        editor = p.Editor.ToString(),
        defaultValue = p.Default,
        minimum = p.Minimum,
        maximum = p.Maximum,
        unit = p.Unit,
        description = p.Description,
        options = p.Options.ToArray(),
        modeOverride = p.ModeOverride,
        risk = p.Risk,
        source = p.Source,
        optionLabels = p.OptionLabels.ToArray(),
        firmwareMayAddMore = p.FirmwareMayAddMore,
        alsoCalled = p.AlsoCalled,
    }).ToArray(),
    defaultTemplate = template,
};

var rendered = JsonSerializer.Serialize(result, new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
}) + "\n";

if (args.Length == 0)
{
    Console.Write(rendered);
}
else if (args.Length == 1)
{
    var output = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, rendered, new UTF8Encoding(false));
}
else
{
    throw new InvalidOperationException("Usage: qcm-catalog-oracle [output-file]");
}

static string[] Sorted(IEnumerable<string> values) =>
    values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "QuadStick.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Run qcm-catalog-oracle from inside the repository.");
}
