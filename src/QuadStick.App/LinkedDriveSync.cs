using System.Collections.Concurrent;

namespace QuadStick.App;

/// <summary>
/// Drains Drive's durable change log without creating the classic cursor-loss
/// bug. Relevant work is durably enqueued BEFORE the new cursor is committed.
/// A crash after enqueue but before cursor commit merely replays a fileId; the
/// durable queue must dedupe by fileId. A crash after cursor commit cannot lose
/// work because the queue was already written.
/// </summary>
public sealed class LinkedDriveChangeTracker
{
    readonly LinkedDriveClient _client;

    public LinkedDriveChangeTracker(LinkedDriveClient client) => _client = client;

    public async Task<string> DrainAsync(
        string pageToken,
        string? driveId,
        IReadOnlySet<string> linkedFileIds,
        Func<IReadOnlyCollection<LinkedDriveChange>, CancellationToken, Task> enqueueDurably,
        Func<string, CancellationToken, Task> commitCursorDurably,
        CancellationToken ct = default)
    {
        var token = pageToken;
        var relevant = new Dictionary<string, LinkedDriveChange>(StringComparer.Ordinal);
        string? newStart = null;

        while (true)
        {
            var page = await _client.ListChangesAsync(token, driveId, ct);
            foreach (var change in page.Changes)
            {
                if (change.ChangeType != "file" || string.IsNullOrWhiteSpace(change.FileId)) continue;
                if (!linkedFileIds.Contains(change.FileId)) continue;
                // The newest event for one file is enough: sync always fetches
                // current state rather than replaying intermediate revisions.
                relevant[change.FileId] = change;
            }

            if (!string.IsNullOrWhiteSpace(page.NextPageToken))
            {
                token = page.NextPageToken!;
                continue;
            }

            newStart = page.NewStartPageToken;
            break;
        }

        // ORDER IS AN INVARIANT. Do not combine these into one "save state"
        // call unless that storage layer is genuinely transactional.
        if (relevant.Count > 0)
            await enqueueDurably(relevant.Values.ToList(), ct);

        var committed = !string.IsNullOrWhiteSpace(newStart) ? newStart! : token;
        await commitCursorDurably(committed, ct);
        return committed;
    }
}

/// <summary>Numeric Drive/Sheets cell identity, zero-based row/column.</summary>
public readonly record struct LinkedCellKey(int SheetId, int RowIndex, int ColumnIndex);

/// <summary>
/// Parsed QuadStick projection used for merge. StructureSignature must change
/// when tabs, mode identity, binding rows, or their ordering changes. That lets
/// v1 safely auto-merge ordinary cell edits while escalating insert/delete/
/// reorder operations instead of pretending row numbers are stable identities.
/// </summary>
public sealed record LinkedProfileSnapshot(
    string StructureSignature,
    IReadOnlyDictionary<LinkedCellKey, string> Cells,
    IReadOnlySet<LinkedCellKey>? FormulaCells = null,
    IReadOnlySet<LinkedCellKey>? ProtectedCells = null);

public enum LinkedMergeConflictKind
{
    SameCellChangedDifferently,
    StructuralChange,
    FormulaCell,
    ProtectedCell,
}

public sealed record LinkedMergeConflict(
    LinkedMergeConflictKind Kind,
    LinkedCellKey? Cell = null,
    string? BaseValue = null,
    string? LocalValue = null,
    string? RemoteValue = null);

public sealed record LinkedMergeResult(
    bool CanAutoMerge,
    IReadOnlyDictionary<LinkedCellKey, string> MergedCells,
    IReadOnlyList<LinkedMergeConflict> Conflicts,
    IReadOnlyList<LinkedSheetCellUpdate> RemoteUpdates);

/// <summary>
/// Conservative BASE/LOCAL/REMOTE merge. No last-writer-wins data loss:
/// - different cells merge automatically;
/// - same cell with different values conflicts;
/// - formula/protected cells never get overwritten automatically;
/// - any structural divergence is an explicit structural conflict in v1.
/// </summary>
public static class LinkedProfileMerge
{
    public static LinkedMergeResult Merge(
        LinkedProfileSnapshot @base,
        LinkedProfileSnapshot local,
        LinkedProfileSnapshot remote)
    {
        if (!string.Equals(@base.StructureSignature, local.StructureSignature, StringComparison.Ordinal) ||
            !string.Equals(@base.StructureSignature, remote.StructureSignature, StringComparison.Ordinal))
        {
            return new LinkedMergeResult(
                false,
                new Dictionary<LinkedCellKey, string>(local.Cells),
                new[] { new LinkedMergeConflict(LinkedMergeConflictKind.StructuralChange) },
                Array.Empty<LinkedSheetCellUpdate>());
        }

        var formula = remote.FormulaCells ?? EmptySet<LinkedCellKey>.Instance;
        var protectedCells = remote.ProtectedCells ?? EmptySet<LinkedCellKey>.Instance;
        var keys = new HashSet<LinkedCellKey>(@base.Cells.Keys);
        keys.UnionWith(local.Cells.Keys);
        keys.UnionWith(remote.Cells.Keys);

        var merged = new Dictionary<LinkedCellKey, string>();
        var conflicts = new List<LinkedMergeConflict>();
        var remoteUpdates = new List<LinkedSheetCellUpdate>();

        foreach (var key in keys.OrderBy(k => k.SheetId).ThenBy(k => k.RowIndex).ThenBy(k => k.ColumnIndex))
        {
            var b = Value(@base.Cells, key);
            var l = Value(local.Cells, key);
            var r = Value(remote.Cells, key);
            bool localChanged = !string.Equals(l, b, StringComparison.Ordinal);
            bool remoteChanged = !string.Equals(r, b, StringComparison.Ordinal);

            if (localChanged && formula.Contains(key))
            {
                conflicts.Add(new LinkedMergeConflict(LinkedMergeConflictKind.FormulaCell, key, b, l, r));
                merged[key] = r;
                continue;
            }
            if (localChanged && protectedCells.Contains(key))
            {
                conflicts.Add(new LinkedMergeConflict(LinkedMergeConflictKind.ProtectedCell, key, b, l, r));
                merged[key] = r;
                continue;
            }
            if (localChanged && remoteChanged && !string.Equals(l, r, StringComparison.Ordinal))
            {
                conflicts.Add(new LinkedMergeConflict(
                    LinkedMergeConflictKind.SameCellChangedDifferently, key, b, l, r));
                // Conflict is unresolved. Keep the user's local working value
                // in the editor, but do not create a remote write for it.
                merged[key] = l;
                continue;
            }

            var value = localChanged ? l : remoteChanged ? r : b;
            merged[key] = value;
            if (localChanged && !string.Equals(value, r, StringComparison.Ordinal))
                remoteUpdates.Add(new LinkedSheetCellUpdate(
                    key.SheetId, key.RowIndex, key.ColumnIndex, value));
        }

        return new LinkedMergeResult(conflicts.Count == 0, merged, conflicts, remoteUpdates);
    }

    static string Value(IReadOnlyDictionary<LinkedCellKey, string> cells, LinkedCellKey key) =>
        cells.TryGetValue(key, out var value) ? value : "";

    sealed class EmptySet<T> : IReadOnlySet<T>
    {
        public static readonly EmptySet<T> Instance = new();
        public int Count => 0;
        public bool Contains(T item) => false;
        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<T> other) => false;
        public bool IsProperSupersetOf(IEnumerable<T> other) => !other.Any();
        public bool IsSubsetOf(IEnumerable<T> other) => true;
        public bool IsSupersetOf(IEnumerable<T> other) => !other.Any();
        public bool Overlaps(IEnumerable<T> other) => false;
        public bool SetEquals(IEnumerable<T> other) => !other.Any();
    }
}

/// <summary>
/// Per-document serialization and generation tracking. A remote fetch may run
/// while a human keeps editing locally; the network result is merged under the
/// gate against the CURRENT generation rather than blindly replacing the
/// editor with the stale state that existed when the fetch began.
/// </summary>
public sealed class LinkedDocumentCoordinator
{
    sealed class State
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Generation;
    }

    readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public long MarkLocalEdit(string linkKey) =>
        Interlocked.Increment(ref _states.GetOrAdd(linkKey, _ => new State()).Generation);

    public long CurrentGeneration(string linkKey) =>
        Volatile.Read(ref _states.GetOrAdd(linkKey, _ => new State()).Generation);

    public async Task<T> RunExclusiveAsync<T>(
        string linkKey, Func<long, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(linkKey, _ => new State());
        await state.Gate.WaitAsync(ct);
        try
        {
            return await operation(Volatile.Read(ref state.Generation), ct);
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
