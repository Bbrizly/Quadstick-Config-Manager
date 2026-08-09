namespace QuadStick.Format;

// Turn an arbitrary Google Sheets name into a CSV file name that is safe
// on both macOS and Windows.
public static class SafeFileName
{
    static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Firmware 2373 keeps the root file list in `char files[NUM_FILES][32]` and
    // fills each slot with strncpy(..., 32), which leaves no null terminator
    // when the name is 32 characters or longer. The device then prints and
    // opens one run-on string, so the file selector shows garbage and the
    // profile cannot be loaded at all. 31 characters is the longest name that
    // still ends inside its slot. Firmware 1476 used a 255 byte slot, so a long
    // name that works today stops working after the user updates the device.
    public const int MaxDeviceFileNameLength = 31;

    // ".csv" spends four of the 31.
    const int MaxBaseLength = MaxDeviceFileNameLength - 4;

    public static bool IsTooLongForDevice(string? fileName) =>
        (fileName ?? "").Length > MaxDeviceFileNameLength;

    // Chars this platform rejects, plus / \ : which are legal on macOS but
    // break a synced file on Windows. Must be safe on both.
    static readonly HashSet<char> InvalidChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\', ':' }).ToHashSet();

    // Windows resolves these to devices whatever extension follows them, so
    // "NUL.csv" is not a file at all: the write succeeds, the readback comes
    // back empty, and the user is told verification failed rather than that
    // their profile cannot be called that.
    public static bool IsReservedOnWindows(string? fileName) =>
        ReservedWindowsNames.Contains(Path.GetFileNameWithoutExtension(fileName ?? ""));

    public static string ForCsv(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "Untitled.csv";

        if (trimmed.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var cleaned = string.Concat(trimmed.Select(c => InvalidChars.Contains(c) ? '_' : c));

        // Windows rejects a trailing dot or space.
        var baseName = cleaned.TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "Untitled";

        if (ReservedWindowsNames.Contains(baseName)) baseName += "_file";

        // The cap can cut after a dot or space, so trim again.
        if (baseName.Length > MaxBaseLength) baseName = baseName[..MaxBaseLength].TrimEnd('.', ' ');
        if (baseName.Length == 0) baseName = "Untitled";

        return baseName + ".csv";
    }

    // Same thing, but never hands back a name already in `taken`. Cutting the
    // base to 27 characters makes two different sheet names land on the same
    // file far more often than the old 100 character cut did, and two profiles
    // that share a first 27 characters are still two profiles. The number has
    // to come out of the 27, not be added on top, or the unique name is the one
    // that no longer fits on the device. Matching ignores case because the
    // device lowercases every name before it stores or compares it.
    public static string ForCsv(string? name, IEnumerable<string> taken)
    {
        var claimed = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var first = ForCsv(name);
        if (!claimed.Contains(first)) return first;

        var stem = first[..^4];
        for (int n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var room = MaxBaseLength - suffix.Length;
            var head = (stem.Length > room ? stem[..room] : stem).TrimEnd('.', ' ');
            if (head.Length == 0) head = "Untitled";
            var candidate = head + suffix + ".csv";
            if (!claimed.Contains(candidate)) return candidate;
        }
    }
}
