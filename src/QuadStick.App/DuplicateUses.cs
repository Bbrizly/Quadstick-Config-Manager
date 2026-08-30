using QuadStick.Format;

namespace QuadStick.App;

// The Sheets extension paints a cell when the same output or input appears
// elsewhere in the mode, and a tester said losing it cost them "a lot of
// mental memorization" and a combination they never noticed was still free.
// This is the fact behind that mark: how many rows of one mode carry each
// token. The window turns a count above one into a chip on the cell.
//
// Counts are per mode, because a mode is what the device runs at one time.
// The same output in two modes is ordinary; twice in one mode is worth
// seeing. Ordinal, like the firmware's own string compare.
public static class DuplicateUses
{
    public sealed record Counts(
        IReadOnlyDictionary<string, int> Outputs,
        IReadOnlyDictionary<string, int> Inputs)
    {
        public static readonly Counts None = new(
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal));

        public int Output(string token) => Outputs.GetValueOrDefault(token.Trim());
        public int Input(string token) => Inputs.GetValueOrDefault(token.Trim());
    }

    public static Counts In(IEnumerable<Binding>? bindings)
    {
        if (bindings is null) return Counts.None;
        var outputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, int>(StringComparer.Ordinal);

        static void Bump(Dictionary<string, int> into, string token)
        {
            token = token.Trim();
            if (token.Length > 0) into[token] = into.GetValueOrDefault(token) + 1;
        }

        foreach (var b in bindings)
        {
            // An action name is what the row is called, not what it fires, so
            // the output token underneath is what repeats.
            Bump(outputs, b.Output);
            // Per cell, like the spreadsheet: one row naming an input twice is
            // itself a duplicate worth seeing.
            foreach (var i in b.Inputs) Bump(inputs, i);
        }
        return new Counts(outputs, inputs);
    }
}
