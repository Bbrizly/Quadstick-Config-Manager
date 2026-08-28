namespace QuadStick.Format;

/// <summary>Optional neutral format metadata applied while serializing a
/// profile. Core knows only that the header can carry a source sheet id; it does
/// not know which provider supplied it.</summary>
public sealed record ProfileSerializationContext(string? SourceSheetId = null);

public static class ProfileCsvSerializer
{
    public static string Serialize(
        ProfileSnapshot snapshot,
        ProfileSerializationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var working = ProfileFile.Load(snapshot.RawCsvText);
        StampSourceSheetId(working, context?.SourceSheetId);
        return working.ToCsvText();
    }

    internal static void StampSourceSheetId(ProfileFile working, string? sourceSheetId)
    {
        if (sourceSheetId is null || !working.Document.HasVersionHeader) return;
        // This is an isolated serialization copy. Using the workspace's normal
        // mutation path avoids a second hidden grid-stamping mechanism; its
        // dirty/undo bookkeeping is irrelevant because this copy is discarded.
        working.SetCell(1, 2, sourceSheetId);
    }
}

public static class DeviceProfileSerializer
{
    public static string Serialize(
        ProfileSnapshot snapshot,
        ProfileSerializationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var working = ProfileFile.Load(snapshot.RawCsvText);
        ProfileCsvSerializer.StampSourceSheetId(working, context?.SourceSheetId);
        working.NormalizeForDeviceCsv();
        return working.ToCsvText();
    }
}
