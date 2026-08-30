using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class LinkedDriveStateStoreTests
{
    [Fact]
    public async Task Change_work_is_durable_before_cursor_advances()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new LinkedDriveStateStore(dir);
            var stream = store.StreamKey("acct", null);
            var observedOrder = new List<string>();
            var tracker = new LinkedDriveChangeTracker((token, _, _) => Task.FromResult(
                new LinkedDriveChangePage(
                    new[] { new LinkedDriveChange("file-1", false, "file", "", null, null) },
                    null,
                    "cursor-2")));

            var result = await tracker.DrainAsync(
                "cursor-1", null, new HashSet<string> { "file-1" },
                async (changes, ct) =>
                {
                    await store.EnqueueChangesAsync(stream, changes, ct);
                    Assert.Contains("file-1", store.ReadPendingFileIds(stream));
                    Assert.Null(store.ReadCursor(stream));
                    observedOrder.Add("queue");
                },
                async (cursor, ct) =>
                {
                    Assert.Contains("file-1", store.ReadPendingFileIds(stream));
                    observedOrder.Add("cursor");
                    await store.CommitCursorAsync(stream, cursor, ct);
                });

            Assert.Equal("cursor-2", result);
            Assert.Equal(new[] { "queue", "cursor" }, observedOrder);
            Assert.Equal("cursor-2", store.ReadCursor(stream));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Replay_deduplicates_pending_file_ids()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new LinkedDriveStateStore(dir);
            var stream = store.StreamKey("acct", "shared-drive");
            var changes = new[]
            {
                new LinkedDriveChange("same-file", false, "file", "", "shared-drive", null),
            };

            await store.EnqueueChangesAsync(stream, changes);
            await store.EnqueueChangesAsync(stream, changes);

            Assert.Equal(new[] { "same-file" }, store.ReadPendingFileIds(stream));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Snapshot_round_trips_structure_values_and_read_only_guards()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new LinkedDriveStateStore(dir);
            var link = store.LinkKey("acct", "file");
            var path = store.BaseSnapshotPath(link);
            var formula = new LinkedCellKey(7, 1, 2);
            var protectedCell = new LinkedCellKey(7, 3, 4);
            var snapshot = new LinkedProfileSnapshot(
                "modes\nrows\tstructure",
                new Dictionary<LinkedCellKey, string>
                {
                    [formula] = "value\nwith\ttabs",
                    [protectedCell] = "other",
                },
                new HashSet<LinkedCellKey> { formula },
                new HashSet<LinkedCellKey> { protectedCell });

            store.SaveSnapshot(path, snapshot);
            var loaded = Assert.IsType<LinkedProfileSnapshot>(store.LoadSnapshot(path));

            Assert.Equal(snapshot.StructureSignature, loaded.StructureSignature);
            Assert.Equal(snapshot.Cells[formula], loaded.Cells[formula]);
            Assert.Contains(formula, loaded.FormulaCells!);
            Assert.Contains(protectedCell, loaded.ProtectedCells!);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Acknowledge_removes_only_completed_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new LinkedDriveStateStore(dir);
            var stream = store.StreamKey("acct", null);
            store.EnqueueChangesAsync(stream, new[]
            {
                new LinkedDriveChange("a", false, "file", "", null, null),
                new LinkedDriveChange("b", false, "file", "", null, null),
            }).GetAwaiter().GetResult();

            store.AcknowledgePending(stream, "a");

            Assert.Equal(new[] { "b" }, store.ReadPendingFileIds(stream));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
