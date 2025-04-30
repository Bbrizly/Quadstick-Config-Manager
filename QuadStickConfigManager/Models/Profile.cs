using System.Collections.ObjectModel;

namespace QSCM.Models;

public record ConfigRow(
    string Input,     // sip, puff, lip, etc.
    string Output,    // A, B, MouseLeft…
    string Function); // normal, latch, repeat…

public class Profile
{
    public string Game { get; }
    public ObservableCollection<ConfigRow> Rows { get; }

    public Profile(string game, IEnumerable<ConfigRow> rows)
    {
        Game = game;
        Rows = new ObservableCollection<ConfigRow>(rows);
    }
}
