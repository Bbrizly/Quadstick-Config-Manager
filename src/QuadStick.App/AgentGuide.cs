using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// The profile the agent worked out, drawn on the device it is for, and walked
// through one part at a time before anybody is asked anything.
//
// A list of forty rows saying kb_space, mp_left_puff_soft is a correct answer
// nobody can check. The same forty rows shown as "Jump: soft puff on the left
// hole" on a picture of the mouthpiece is the same answer, and it is one a
// person can disagree with. That is the whole point of this file: the approval
// at the end is worth nothing if what came before it was unreadable.
//
// Nothing here decides anything. It draws what the run already sent and hands
// answers straight back.

/// <summary>One control on the device: the game's word for it, what triggers
/// it, and whether it is still a question.</summary>
internal sealed record Placed(string Output, string Action, IReadOnlyList<string> Inputs,
                              string Function, string Why, bool Asking = false,
                              bool Critical = false)
{
    /// <summary>The part of the device this lands on. A control with nothing
    /// triggering it yet is not on the device at all, and says so.</summary>
    internal string Zone => Inputs.Count > 0 ? MainWindow.ZoneOf(Inputs[0]) : "unset";

    /// <summary>What to call it. The game's own word when the chart had one,
    /// and the device token when it did not, never a guess in between.</summary>
    internal string Name => Action.Length > 0 ? Action : Output;

    /// <summary>What you do to fire it, in the words of the part it is on:
    /// "soft puff", not "mp_left_puff_soft".</summary>
    internal string Trigger => Inputs.Count == 0
        ? "nothing triggers it yet"
        : string.Join(" and ", Inputs.Select(i => MainWindow.StripInput(i, Zone)));
}

/// <summary>The device, drawn from the parts list Device View uses, with the
/// controls that landed on each part. One part can be lit at a time.</summary>
internal sealed class DeviceMap : Border
{
    readonly Dictionary<string, ZoneTile> _tiles = new();
    string? _lit;

    /// <summary>The parts that got drawn, top to bottom, as the walkthrough
    /// steps through them.</summary>
    internal IReadOnlyList<string> Parts { get; }

    internal DeviceMap(IReadOnlyList<Placed> rows)
    {
        CornerRadius = new CornerRadius(AgentWindow.Size("TrackRadius"));
        Padding = new Thickness(14, 14);
        BorderThickness = new Thickness(1);
        AgentWindow.Paint(this, "SurfaceSubtleBrush", "SurfaceBorderBrush");

        var byZone = rows.GroupBy(r => r.Zone).ToDictionary(g => g.Key, g => (IReadOnlyList<Placed>)g.ToList());
        // The parts every QuadStick has are drawn whether or not anything landed
        // on them. An empty left hole is a fact worth seeing: it is where the
        // next thing they want can go. It also means an answer that lands
        // somewhere nothing else did still has a part to light up.
        var core = new[] { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip" };
        var shown = MainWindow.AllZones
            .Where(z => core.Contains(z.Id) || byZone.ContainsKey(z.Id))
            .ToList();
        Parts = shown.Select(z => z.Id).ToList();

        foreach (var zone in shown)
            _tiles[zone.Id] = new ZoneTile(zone, byZone.GetValueOrDefault(zone.Id, Array.Empty<Placed>()));

        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        Tile("joystick", 240, stack);

        var holes = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var id in new[] { "mp_left", "mp_center", "mp_right" })
            if (_tiles.TryGetValue(id, out var tile))
            { tile.Width = 128; tile.Margin = new Thickness(4); holes.Children.Add(tile); }
        stack.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "Mouthpiece", FontSize = AgentWindow.Size("SmallSize"),
                    FontWeight = FontWeight.Bold, Classes = { "muted" },
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                holes,
            },
        });

        Tile("combo", 400, stack);
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (var id in new[] { "side", "lip" })
            if (_tiles.TryGetValue(id, out var tile)) { tile.Width = 195; pair.Children.Add(tile); }
        if (pair.Children.Count > 0) stack.Children.Add(pair);

        var rest = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var id in new[] { "jacks", "other", "settings", "unset" })
            if (_tiles.TryGetValue(id, out var tile))
            { tile.Width = 195; tile.Margin = new Thickness(4); rest.Children.Add(tile); }
        if (rest.Children.Count > 0) stack.Children.Add(rest);

        Child = stack;
        AutomationProperties.SetName(this, "Your QuadStick, with what each part does in this game");
    }

    void Tile(string id, double width, StackPanel into)
    {
        if (!_tiles.TryGetValue(id, out var tile)) return;
        tile.Width = width;
        into.Children.Add(tile);
    }

    /// <summary>Light one part, or none. The lit part carries a mark and the
    /// word "showing" in its own text, never the border colour alone.</summary>
    internal void Highlight(string? zone)
    {
        if (_lit is not null && _tiles.TryGetValue(_lit, out var was)) was.Lit = false;
        // A part that is not drawn cannot be lit. Recording it as lit anyway
        // would have this quietly claim to be showing something it is not, and
        // the option's own words are already carrying where it lands.
        _lit = zone is not null && _tiles.ContainsKey(zone) ? zone : null;
        if (_lit is not null) _tiles[_lit].Lit = true;
    }

    /// <summary>Test seam: what one part says, as it reads on screen.</summary>
    internal string TextOf(string zone) => _tiles.TryGetValue(zone, out var tile) ? tile.Words : "";

    internal bool IsLit(string zone) => _lit == zone;

    sealed class ZoneTile : Border
    {
        readonly TextBlock _title;
        readonly string _name;
        bool _lit;

        internal string Words { get; }

        internal ZoneTile(MainWindow.Zone zone, IReadOnlyList<Placed> rows)
        {
            _name = zone.Title;
            CornerRadius = new CornerRadius(AgentWindow.Size("TrackRadius"));
            Padding = new Thickness(10, 8);
            BorderThickness = new Thickness(1);
            var body = new StackPanel { Spacing = 2 };
            _title = new TextBlock
            {
                Text = zone.Title, FontSize = AgentWindow.Size("SmallSize"),
                FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            };
            body.Children.Add(_title);
            if (rows.Count == 0)
                body.Children.Add(new TextBlock
                {
                    Text = "nothing here", FontSize = AgentWindow.Size("SmallSize"),
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                });
            foreach (var row in rows.Take(4))
                body.Children.Add(new TextBlock
                {
                    Text = row.Asking ? $"{row.Name}?" : row.Name,
                    FontSize = AgentWindow.Size("SmallSize"), TextWrapping = TextWrapping.Wrap,
                });
            if (rows.Count > 4)
                body.Children.Add(new TextBlock
                {
                    Text = $"and {rows.Count - 4} more", FontSize = AgentWindow.Size("SmallSize"),
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                });
            Child = body;
            Words = rows.Count == 0 ? $"{zone.Title}: nothing here"
                : $"{zone.Title}: {string.Join(", ", rows.Select(r => r.Name))}";
            AutomationProperties.SetName(this, Words);
            Lit = false;
        }

        internal bool Lit
        {
            get => _lit;
            set
            {
                _lit = value;
                BorderThickness = new Thickness(value ? 2 : 1);
                AgentWindow.Paint(this, value ? "SurfaceBrush" : "SurfaceSubtleBrush",
                                  value ? "AccentBrush" : "SurfaceBorderBrush");
                // The mark is a second signal. Anyone who cannot see the border
                // still gets the part named in the sentence above the picture.
                _title.Text = value ? "▸ " + _name : _name;
            }
        }
    }
}

/// <summary>The device with a sentence above it: a walkthrough of what was
/// worked out, and then the questions, asked over the same picture.</summary>
internal sealed class AgentGuide : DockPanel
{
    readonly DeviceMap _map;
    readonly StackPanel _above;
    readonly StackPanel _below;
    readonly List<Step> _steps = new();
    readonly IReadOnlyList<Placed> _open;
    readonly IReadOnlyList<Placed> _left;
    int _at = -1;

    sealed record Step(string? Zone, string Heading, string Body, IReadOnlyList<Placed> Rows);

    /// <summary>The walkthrough ran out. Whatever comes next, a question or the
    /// approval, happens now.</summary>
    internal Action? Walked { get; set; }

    /// <summary>Still stepping through parts. A question that arrives while
    /// this is true waits: interrupting somebody halfway through being shown
    /// their own device is how you get an answer they did not think about.</summary>
    internal bool Walking => _at >= 0 && _at < _steps.Count;

    /// <summary>Test seam: everything said above the device right now.</summary>
    internal string Saying => string.Join("\n", Texts(_above));

    static IEnumerable<string> Texts(Panel panel) => panel.Children
        .SelectMany(child => child switch
        {
            TextBlock text => new[] { text.Text ?? "" },
            Panel inner => Texts(inner),
            _ => Array.Empty<string>(),
        })
        .Where(said => said.Length > 0);

    internal DeviceMap Map => _map;

    internal AgentGuide(string game, IReadOnlyList<Placed> rows,
                        IReadOnlyList<Placed> open, IReadOnlyList<Placed> left)
    {
        _open = open;
        _left = left;
        var asking = open.Count;
        _above = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 14) };
        _below = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) };
        _map = new DeviceMap(rows);

        var bound = rows.Count(r => !r.Asking);
        var opening = $"{bound} control{(bound == 1 ? "" : "s")} worked out from the "
                    + "profiles you have already built.";
        if (asking > 0)
            opening += $" {asking} still need{(asking == 1 ? "s" : "")} you.";
        if (left.Count > 0)
            opening += $" {left.Count} {(left.Count == 1 ? "is" : "are")} left unbound on purpose.";
        _steps.Add(new Step(null, $"{game}, on your QuadStick", opening, rows));

        foreach (var id in _map.Parts)
        {
            var zone = MainWindow.AllZones.First(z => z.Id == id);
            var here = rows.Where(r => r.Zone == id).ToList();
            if (here.Count == 0) continue;
            _steps.Add(new Step(id, zone.Title, zone.Blurb, here));
        }

        _steps.Add(new Step(null,
            asking > 0 ? $"{asking} thing{(asking == 1 ? "" : "s")} the evidence cannot settle"
                       : "That is the whole profile",
            asking > 0
                ? "These are the ones your own profiles answer both ways, so they are yours "
                + "to call. Each one shows where it would land on the device."
                : "Nothing here needs you. Next is the list to approve, and nothing is "
                + "written until you do.",
            Array.Empty<Placed>()));

        DockPanel.SetDock(_above, Dock.Top);
        DockPanel.SetDock(_below, Dock.Bottom);
        Children.Add(_above);
        Children.Add(_below);
        Children.Add(new ScrollViewer
        {
            Content = _map,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });
        Go(0);
    }

    // ---- the walkthrough --------------------------------------------------

    void Go(int step)
    {
        _at = Math.Clamp(step, 0, _steps.Count - 1);
        var now = _steps[_at];
        _map.Highlight(now.Zone);

        _above.Children.Clear();
        _above.Children.Add(Heading(now.Heading));
        _above.Children.Add(Body(now.Body));
        // Every control on this part, in the game's words, with what fires it
        // and why it is there. This is the part somebody actually reads. The
        // opening step has no part, so it stays a count rather than listing the
        // whole profile before they have seen any of the device.
        if (now.Zone is not null)
            foreach (var row in now.Rows)
                _above.Children.Add(Line(row));
        // The last step is where what is NOT being bound gets said, by name and
        // with its reason. A control left on purpose and a control nobody got
        // to are not the same thing, and neither of them is allowed to be a
        // number somebody has to go looking behind.
        if (_at == _steps.Count - 1)
        {
            foreach (var open in _open) _above.Children.Add(Aside(open.Name, open.Why));
            if (_left.Count > 0)
                _above.Children.Add(Body($"{_left.Count} left unbound on purpose:"));
            foreach (var left in _left) _above.Children.Add(Aside(left.Name, left.Why));
        }
        _above.Children.Add(new TextBlock
        {
            Text = $"Step {_at + 1} of {_steps.Count}",
            FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
        });

        _below.Children.Clear();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (_at > 0) buttons.Children.Add(Button("Back", () => Go(_at - 1)));
        var last = _at == _steps.Count - 1;
        buttons.Children.Add(Button(last ? "Continue" : "Next", () =>
        {
            if (last) { _at = _steps.Count; Walked?.Invoke(); }
            else Go(_at + 1);
        }, primary: true));
        if (!last)
            buttons.Children.Add(Button("Skip the walkthrough", () =>
            {
                _at = _steps.Count;
                _map.Highlight(null);
                Walked?.Invoke();
            }, quiet: true));
        _below.Children.Add(buttons);
    }

    // ---- a question, asked over the same picture --------------------------

    /// <summary>One question, above the device, with each option lighting the
    /// part it would land on as it is reached. Reached by keyboard as well as
    /// by mouse: focus lights the part, so tabbing through the options walks
    /// the device.</summary>
    internal void Ask(string question, string about, IReadOnlyList<Placed> options,
                      Action<int> answer)
    {
        _map.Highlight(null);
        _above.Children.Clear();
        _above.Children.Add(Heading(question));
        _above.Children.Add(new TextBlock
        {
            Text = about, FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
        });

        _below.Children.Clear();
        var all = new List<Button>();
        for (int n = 0; n < options.Count; n++)
        {
            var option = options[n];
            var choice = n;
            var where = option.Inputs.Count > 0
                ? $"{option.Trigger} on the {Title(option.Zone)}, {MainWindow.FunctionExplain(option.Function)}"
                : "leaves it unbound";
            var button = new Button
            {
                MinHeight = AgentWindow.Size("ControlHeight"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = option.Name, FontSize = AgentWindow.Size("BodySize"),
                                        TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = where, FontSize = AgentWindow.Size("SmallSize"),
                                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            AutomationProperties.SetName(button, $"{option.Name}. {where}.");
            void Light() => _map.Highlight(option.Inputs.Count > 0 ? option.Zone : null);
            button.GotFocus += (_, _) => Light();
            button.PointerEntered += (_, _) => Light();
            button.Click += (_, _) =>
            {
                foreach (var b in all) b.IsEnabled = false;
                answer(choice);
            };
            all.Add(button);
            _below.Children.Add(button);
        }
    }

    /// <summary>The question is answered. Say what was chosen, over the part it
    /// landed on, and stop offering to change it here.</summary>
    internal void Chose(string what, Placed? landed)
    {
        _map.Highlight(landed is { Inputs.Count: > 0 } ? landed.Zone : null);
        _below.Children.Clear();
        _above.Children.Add(new TextBlock
        {
            Text = $"You chose: {what}", FontSize = AgentWindow.Size("BodySize"),
            FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>Nothing left to ask here. Says so rather than sitting on the
    /// last thing that happened, which reads as a run that stopped.</summary>
    internal void Waiting(string text)
    {
        _map.Highlight(null);
        _above.Children.Clear();
        _above.Children.Add(Heading(text));
        _below.Children.Clear();
    }

    // ---- pieces -----------------------------------------------------------

    static string Title(string zone) =>
        MainWindow.AllZones.FirstOrDefault(z => z.Id == zone)?.Title.ToLowerInvariant() ?? zone;

    static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("SectionSize"), FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap,
    };

    static TextBlock Body(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("BodySize"), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A control that is not being bound: what it is, and why not.</summary>
    static Control Aside(string name, string why)
    {
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = name, FontSize = AgentWindow.Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = why, FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
        });
        AutomationProperties.SetName(stack, $"{name}. {why}");
        return stack;
    }

    static Control Line(Placed row)
    {
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 0) };
        var said = $"{row.Name}: {row.Trigger}, {MainWindow.FunctionExplain(row.Function)}";
        stack.Children.Add(new TextBlock
        {
            Text = said, FontSize = AgentWindow.Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        if (row.Why.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = row.Why, FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
        AutomationProperties.SetName(stack, $"{said}. {row.Why}");
        return stack;
    }

    static Button Button(string text, Action clicked, bool primary = false, bool quiet = false)
    {
        var button = new Button
        {
            Content = text, MinWidth = 120, MinHeight = AgentWindow.Size("ControlHeight"),
        };
        if (primary) button.Classes.Add("primary");
        if (quiet) button.Classes.Add("quiet");
        button.Click += (_, _) => clicked();
        return button;
    }
}
