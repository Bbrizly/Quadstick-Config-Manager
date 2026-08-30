using System.Security.Cryptography;
using System.Text;

namespace QuadStick.App;

/// <summary>
/// Small crash-safe sidecar store for linked-Drive synchronization.
///
/// settings.json is intentionally not the transaction log. Change cursors,
/// pending remote file ids and BASE snapshots must survive an app crash in a
/// known order, so each is an independently atomic file under app data.
/// </summary>
public sealed class LinkedDriveStateStore
{
    public static string? RootOverride { get; set; }

    public static string DefaultRoot => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QuadStickConfigManager", "drive-sync");

    readonly string _root;

    public LinkedDriveStateStore(string? root = null) => _root = root ?? DefaultRoot;

    public string LinkKey(string googleAccountId, string driveFileId) =>
        StableKey(googleAccountId + "\n" + driveFileId);

    public string StreamKey(string googleAccountId, string? driveId) =>
        StableKey(googleAccountId + "\n" + (driveId ?? "my-drive"));

    public string? ReadCursor(string streamKey)
    {
        var path = CursorPath(streamKey);
        try
        {
            if (!File.Exists(path)) return null;
            var value = File.ReadAllText(path, Encoding.UTF8).Trim();
            return value.Length == 0 ? null : value;
        }
        catch { return null; }
    }

    public Task CommitCursorAsync(string streamKey, string cursor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WriteAtomicDurable(CursorPath(streamKey), cursor + "\n");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Idempotently queue changed file ids. This MUST complete before a caller
    /// commits the corresponding Drive change cursor.
    /// </summary>
    public Task EnqueueChangesAsync(
        string streamKey, IReadOnlyCollection<LinkedDriveChange> changes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = new HashSet<string>(ReadPendingFileIds(streamKey), StringComparer.Ordinal);
        foreach (var change in changes)
            if (!string.IsNullOrWhiteSpace(change.FileId)) ids.Add(change.FileId);
        WriteQueue(streamKey, ids);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> ReadPendingFileIds(string streamKey)
    {
        var path = QueuePath(streamKey);
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            return File.ReadAllLines(path, Encoding.UTF8)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Acknowledge only after that file has been successfully reconciled or
    /// persisted as an explicit terminal state such as FileRemoved/Conflict.
    /// </summary>
    public void AcknowledgePending(string streamKey, string fileId)
    {
        var ids = new HashSet<string>(ReadPendingFileIds(streamKey), StringComparer.Ordinal);
        if (!ids.Remove(fileId)) return;
        WriteQueue(streamKey, ids);
    }

    public string BaseSnapshotPath(string linkKey) => Path.Combine(LinkDir(linkKey), "base.snapshot");
    public string PendingLocalSnapshotPath(string linkKey) => Path.Combine(LinkDir(linkKey), "local-pending.snapshot");

    public void SaveSnapshot(string path, LinkedProfileSnapshot snapshot)
    {
        var lines = new List<string>
        {
            "QCM-LINKED-SNAPSHOT\t1",
            "S\t" + B64(snapshot.StructureSignature),
        };
        var formulas = snapshot.FormulaCells ?? new HashSet<LinkedCellKey>();
        var protectedCells = snapshot.ProtectedCells ?? new HashSet<LinkedCellKey>();
        foreach (var (key, value) in snapshot.Cells
                     .OrderBy(x => x.Key.SheetId)
                     .ThenBy(x => x.Key.RowIndex)
                     .ThenBy(x => x.Key.ColumnIndex))
        {
            var flags = (formulas.Contains(key) ? "F" : "") + (protectedCells.Contains(key) ? "P" : "");
            lines.Add($"C\t{key.SheetId}\t{key.RowIndex}\t{key.ColumnIndex}\t{flags}\t{B64(value)}");
        }
        WriteAtomicDurable(path, string.Join("\n", lines) + "\n");
    }

    public LinkedProfileSnapshot? LoadSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length < 2 || lines[0] != "QCM-LINKED-SNAPSHOT\t1") return null;
            if (!lines[1].StartsWith("S\t", StringComparison.Ordinal)) return null;

            var signature = UnB64(lines[1][2..]);
            var cells = new Dictionary<LinkedCellKey, string>();
            var formulas = new HashSet<LinkedCellKey>();
            var protectedCells = new HashSet<LinkedCellKey>();

            foreach (var line in lines.Skip(2))
            {
                if (line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length != 6 || parts[0] != "C") return null;
                if (!int.TryParse(parts[1], out var sheetId) ||
                    !int.TryParse(parts[2], out var row) ||
                    !int.TryParse(parts[3], out var column)) return null;
                var key = new LinkedCellKey(sheetId, row, column);
                cells[key] = UnB64(parts[5]);
                if (parts[4].Contains('F')) formulas.Add(key);
                if (parts[4].Contains('P')) protectedCells.Add(key);
            }

            return new LinkedProfileSnapshot(signature, cells, formulas, protectedCells);
        }
        catch { return null; }
    }

    public void DeleteLinkState(string linkKey)
    {
        var dir = LinkDir(linkKey);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { }
    }

    void WriteQueue(string streamKey, IEnumerable<string> ids)
    {
        var sorted = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal);
        WriteAtomicDurable(QueuePath(streamKey), string.Join("\n", sorted) + "\n");
    }

    string CursorPath(string streamKey) => Path.Combine(StreamDir(streamKey), "cursor");
    string QueuePath(string streamKey) => Path.Combine(StreamDir(streamKey), "pending");
    string StreamDir(string streamKey) => Path.Combine(_root, "streams", streamKey);
    string LinkDir(string linkKey) => Path.Combine(_root, "links", linkKey);

    static string StableKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    static string B64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    static string UnB64(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    static void WriteAtomicDurable(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
