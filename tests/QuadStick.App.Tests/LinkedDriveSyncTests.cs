using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class LinkedDriveSyncTests
{
    static readonly LinkedCellKey A = new(11, 2, 3);
    static readonly LinkedCellKey B = new(11, 4, 5);

    static LinkedProfileSnapshot Snap(
        string structure,
        params (LinkedCellKey Key, string Value)[] cells) =>
        new(structure, cells.ToDictionary(x => x.Key, x => x.Value));

    [Fact]
    public void Different_local_and_remote_cells_auto_merge()
    {
        var @base = Snap("same", (A, "a"), (B, "b"));
        var local = Snap("same", (A, "local"), (B, "b"));
        var remote = Snap("same", (A, "a"), (B, "remote"));

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.True(result.CanAutoMerge);
        Assert.Empty(result.Conflicts);
        Assert.Equal("local", result.MergedCells[A]);
        Assert.Equal("remote", result.MergedCells[B]);
        var update = Assert.Single(result.RemoteUpdates);
        Assert.Equal(A.SheetId, update.SheetId);
        Assert.Equal(A.RowIndex, update.RowIndex);
        Assert.Equal(A.ColumnIndex, update.ColumnIndex);
        Assert.Equal("local", update.Value);
    }

    [Fact]
    public void Same_cell_changed_differently_never_uses_last_writer_wins()
    {
        var @base = Snap("same", (A, "a"));
        var local = Snap("same", (A, "local"));
        var remote = Snap("same", (A, "remote"));

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.False(result.CanAutoMerge);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(LinkedMergeConflictKind.SameCellChangedDifferently, conflict.Kind);
        Assert.Equal(A, conflict.Cell);
        Assert.Empty(result.RemoteUpdates);
        // The editor keeps the user's local work visible until they resolve it.
        Assert.Equal("local", result.MergedCells[A]);
    }

    [Fact]
    public void Structural_change_is_never_auto_merged_by_row_number()
    {
        var @base = Snap("rows:1,2", (A, "a"));
        var local = Snap("rows:1,2,3", (A, "a"));
        var remote = Snap("rows:1,2", (A, "remote"));

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.False(result.CanAutoMerge);
        Assert.Equal(LinkedMergeConflictKind.StructuralChange, Assert.Single(result.Conflicts).Kind);
        Assert.Empty(result.RemoteUpdates);
    }

    [Fact]
    public void Formula_cell_is_never_replaced_by_local_calculated_text()
    {
        var @base = Snap("same", (A, "old"));
        var local = Snap("same", (A, "new"));
        var remote = new LinkedProfileSnapshot(
            "same",
            new Dictionary<LinkedCellKey, string> { [A] = "=LOOKUP()" },
            new HashSet<LinkedCellKey> { A });

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.False(result.CanAutoMerge);
        Assert.Equal(LinkedMergeConflictKind.FormulaCell, Assert.Single(result.Conflicts).Kind);
        Assert.Empty(result.RemoteUpdates);
        Assert.Equal("=LOOKUP()", result.MergedCells[A]);
    }

    [Fact]
    public void Protected_cell_is_never_written_automatically()
    {
        var @base = Snap("same", (A, "old"));
        var local = Snap("same", (A, "new"));
        var remote = new LinkedProfileSnapshot(
            "same",
            new Dictionary<LinkedCellKey, string> { [A] = "old" },
            ProtectedCells: new HashSet<LinkedCellKey> { A });

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.False(result.CanAutoMerge);
        Assert.Equal(LinkedMergeConflictKind.ProtectedCell, Assert.Single(result.Conflicts).Kind);
        Assert.Empty(result.RemoteUpdates);
    }

    [Fact]
    public void Same_change_on_both_sides_is_already_resolved()
    {
        var @base = Snap("same", (A, "a"));
        var local = Snap("same", (A, "same-new"));
        var remote = Snap("same", (A, "same-new"));

        var result = LinkedProfileMerge.Merge(@base, local, remote);

        Assert.True(result.CanAutoMerge);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.RemoteUpdates);
        Assert.Equal("same-new", result.MergedCells[A]);
    }
}
