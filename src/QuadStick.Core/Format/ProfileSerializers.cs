namespace QuadStick.Format;

/// <summary>Optional metadata applied while serializing a profile. The source
/// identifier is format metadata; provider APIs remain outside Core.</summary>
public sealed record ProfileSerializationContext(string? SourceSheetId = null);

/// <summary>Serializes a snapshot using the same compatibility rules QCM has
/// historically used for files it writes.</summary>
public static class ProfileCsvSerializer
{
    public static string Serialize(
        ProfileSnapshot snapshot,
        ProfileSerializationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var working = ProfileFile.Load(snapshot.RawCsvText);
        working.HeaderSheetId = context?.SourceSheetId;
        return working.ToCsvText();
    }
}

/// <summary>Produces the exact firmware-safe CSV used for installation. Device
/// normalization happens on an isolated working copy, never the live editor.</summary>
public static class DeviceProfileSerializer
{
    public static string Serialize(
        ProfileSnapshot snapshot,
        ProfileSerializationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var working = ProfileFile.Load(snapshot.RawCsvText);
        working.HeaderSheetId = context?.SourceSheetId;
        working.NormalizeForDeviceCsv();
        return working.ToCsvText();
    }
}