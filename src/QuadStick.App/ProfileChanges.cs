using System.Globalization;
using QuadStick.Format;

namespace QuadStick.App;

/// <summary>What replacing one profile with another would change, in lines a
/// person can read before saying yes. No Avalonia, so it is tested on its own.</summary>
/// <remarks>
/// Modes are matched by name, and mappings within a mode as whole lines
/// (output, behaviour, inputs, action name). An edited mapping therefore shows
/// as one added and one removed, which is what it is. Anything else that
/// differs (preferences, notes, the header) is one line saying so. An empty
/// list means the two are the same profile.
/// </remarks>
public static class ProfileChanges
{
    public static IReadOnlyList<string> Between(ProfileFile? local, ProfileFile incoming)
    {
        var lines = new List<string>();
        if (local is null) { lines.Add(Strings.Follow_LocalUnreadable); return lines; }

        var mine = ByName(local.Document.Sheets);
        var theirs = ByName(incoming.Document.Sheets);

        foreach (var (key, sheet) in theirs)
            if (!mine.ContainsKey(key))
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Follow_ModeAdded, Label(sheet)));
        foreach (var (key, sheet) in mine)
            if (!theirs.ContainsKey(key))
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Follow_ModeRemoved, Label(sheet)));
        foreach (var (key, sheet) in theirs)
        {
            if (!mine.TryGetValue(key, out var old)) continue;
            var (added, removed) = Difference(Mappings(old), Mappings(sheet));
            if (added + removed > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Follow_ModeChanged, Label(sheet), added, removed));
        }

        if (lines.Count == 0 && !SameCells(local, incoming))
            lines.Add(Strings.Follow_OtherChanged);
        return lines;
    }

    // First sheet of each name wins; a profile with two modes of one name is
    // still compared, just less finely.
    static Dictionary<string, ModeSheet> ByName(IEnumerable<ModeSheet> sheets)
    {
        var map = new Dictionary<string, ModeSheet>(StringComparer.Ordinal);
        foreach (var s in sheets) map.TryAdd(s.Type + "|" + s.ModeName, s);
        return map;
    }

    static string Label(ModeSheet s) =>
        s.ModeName.Length > 0 ? s.ModeName
        : s.HeaderLabel.Length > 0 ? s.HeaderLabel
        : s.Type.ToString();

    static List<string> Mappings(ModeSheet s) => s.Bindings
        .Select(b => string.Join("\u001f", new[] { b.Output, b.Function, b.ActionName }.Concat(b.Inputs)))
        .ToList();

    // Multiset difference: two identical lines in one mode are two lines.
    static (int Added, int Removed) Difference(List<string> before, List<string> after)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in before) counts[l] = counts.GetValueOrDefault(l) + 1;
        int added = 0;
        foreach (var l in after)
        {
            if (counts.GetValueOrDefault(l) > 0) counts[l]--;
            else added++;
        }
        return (added, counts.Values.Sum());
    }

    // The header row is left out: its last cell names the sheet a file is
    // backed up to, which differs between two people's copies of the same
    // profile and says nothing about what the QuadStick will do.
    static bool SameCells(ProfileFile a, ProfileFile b)
    {
        var ra = Rows(a);
        var rb = Rows(b);
        return ra.SequenceEqual(rb, StringComparer.Ordinal);
    }

    static List<string> Rows(ProfileFile f)
    {
        var rows = f.Grid
            .Skip(f.Document.HasVersionHeader ? 1 : 0)
            .Select(r => string.Join("\u001f", r.Reverse().SkipWhile(c => c.Length == 0).Reverse()))
            .ToList();
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return rows;
    }
}
