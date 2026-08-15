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
// nobody can check. The same forty rows shown as "Jump, on the left hole, from
// a soft puff" on a picture of the mouthpiece is the same answer, and it is one
// a person can disagree with. That is the whole point of this file: the
// approval at the end is worth nothing if what came before it was unreadable.
//
// The device never moves and never scrolls away. Everything else is written
// above it in a fixed band, because a walkthrough that pushes the thing being
// walked through off the bottom of the screen is not a walkthrough.
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
    /// and the device output said in English when it did not. Never the raw
    /// token: "kb_escape" tells somebody nothing about their own profile.</summary>
    internal string Name => Action.Length > 0 ? Action : AgentGuide.Speak(Output);

    /// <summary>What you do to fire it, in the words of the part it is on:
    /// "soft puff", not "mp_left_puff_soft".</summary>
    internal string Trigger => Inputs.Count == 0
        ? "nothing triggers it yet"
        : string.Join(" and ", Inputs.Select(i => MainWindow.StripInput(i, Zone)));

    /// <summary>How it behaves when fired, when that is not the plain press
    /// everything defaults to. Saying "held down for as long as your input is
    /// active" beside forty rows is noise; saying it beside the one row that
    /// latches is the whole point.</summary>
    internal string Behaviour
    {
        get
        {
            var name = Function.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return name is "" or "normal" ? "" : MainWindow.FunctionExplain(Function);
        }
    }
}

/// <summary>The QuadStick itself: the mouthpiece with its three holes, the lip
/// switch under them and the side tube beside them, all inside the frame that
/// is the joystick, because moving the whole mouthpiece is what the joystick
/// is. One part can be lit at a time.</summary>
internal sealed class DeviceMap : Border
{
    readonly Dictionary<string, Part> _parts = new();
    string? _lit;

    /// <summary>The parts that got drawn, in the order the walkthrough steps
    /// through them.</summary>
    internal IReadOnlyList<string> Parts { get; }

    const double Hole = 64;

    internal DeviceMap(IReadOnlyList<Placed> rows)
    {
        CornerRadius = new CornerRadius(AgentWindow.Size("PanelRadius"));
        Padding = new Thickness(16, 14);
        BorderThickness = new Thickness(1);
        AgentWindow.Paint(this, "SurfaceSubtleBrush", "SurfaceBorderBrush");
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        var byZone = rows.GroupBy(r => r.Zone)
                         .ToDictionary(g => g.Key, g => (IReadOnlyList<Placed>)g.ToList());
        IReadOnlyList<Placed> On(string id) => byZone.GetValueOrDefault(id, Array.Empty<Placed>());

        // The parts every QuadStick has are drawn whether or not anything landed
        // on them. An empty left hole is a fact worth seeing: it is where the
        // next thing they want can go. It also means an answer that lands
        // somewhere nothing else did still has a part to light up.
        var core = new[] { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip" };
        var shown = MainWindow.AllZones
            .Where(z => core.Contains(z.Id) || byZone.ContainsKey(z.Id))
            .ToList();
        Parts = shown.Select(z => z.Id).ToList();
        MainWindow.Zone Of(string id) => shown.First(z => z.Id == id);

        // The three holes, side by side, the way they sit on the mouthpiece.
        var holes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var id in new[] { "mp_left", "mp_center", "mp_right" })
            holes.Children.Add(Keep(Circle(Of(id), On(id))));

        var mouth = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        mouth.Children.Add(holes);
        mouth.Children.Add(Keep(Bar(Of("lip"), On("lip"), Hole * 3 + 24)));

        // The side tube sits beside the mouthpiece, not on it, so it is drawn
        // beside it. Somebody looking for the tube they actually sip on should
        // find it where it is.
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        head.Children.Add(mouth);
        var tube = Keep(Circle(Of("side"), On("side"), narrow: true));
        tube.VerticalAlignment = VerticalAlignment.Top;
        tube.Margin = new Thickness(0, Hole * 0.1, 0, 0);
        head.Children.Add(tube);

        Child = new StackPanel
        {
            Spacing = 10,
            Children = { Keep(Frame(Of("joystick"), On("joystick"), head)), Chips(shown, On) },
        };
        AutomationProperties.SetName(this, "Your QuadStick, with what each part does in this game");
    }

    Part Keep(Part part)
    {
        _parts[part.Id] = part;
        return part;
    }

    /// <summary>The parts that are not something you can point at on the
    /// hardware: two holes at once, a switch in a jack, a setting this mode
    /// changes. They are still on the device and still get walked through.</summary>
    Control Chips(IReadOnlyList<MainWindow.Zone> shown, Func<string, IReadOnlyList<Placed>> on)
    {
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var zone in shown.Where(z => z.Id is "combo" or "jacks" or "other" or "settings" or "unset"))
        {
            var chip = Keep(Chip(zone, on(zone.Id)));
            chip.Margin = new Thickness(4);
            row.Children.Add(chip);
        }
        return row;
    }

    // ---- the pieces of the drawing ----------------------------------------

    static Part Circle(MainWindow.Zone zone, IReadOnlyList<Placed> rows, bool narrow = false)
    {
        var size = narrow ? Hole * 0.8 : Hole;
        var face = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = rows.Count == 0 ? "0" : rows.Count.ToString(),
                FontSize = AgentWindow.Size("SubheadSize"), FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var label = Caption(zone.Display);
        return new Part(zone, rows, face, label, new StackPanel
        {
            Spacing = 5, Width = Hole + 14,
            Children = { face, label },
        });
    }

    static Part Bar(MainWindow.Zone zone, IReadOnlyList<Placed> rows, double width)
    {
        var label = Caption($"{zone.Display} · {rows.Count}");
        var face = new Border
        {
            Width = width, Height = 28, CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            Child = label,
        };
        return new Part(zone, rows, face, label, face);
    }

    static Part Chip(MainWindow.Zone zone, IReadOnlyList<Placed> rows)
    {
        var label = Caption($"{zone.Display} · {rows.Count}");
        var face = new Border
        {
            CornerRadius = new CornerRadius(AgentWindow.Size("ControlRadius")),
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 7),
            Child = label,
        };
        return new Part(zone, rows, face, label, face);
    }

    /// <summary>The joystick, drawn as what it is: the frame the whole
    /// mouthpiece moves inside.</summary>
    static Part Frame(MainWindow.Zone zone, IReadOnlyList<Placed> rows, Control inside)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        void Arrow(string glyph, int column, int row)
        {
            var mark = new TextBlock
            {
                Text = glyph, FontSize = AgentWindow.Size("BodySize"), Classes = { "muted" },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 2),
            };
            Grid.SetColumn(mark, column);
            Grid.SetRow(mark, row);
            grid.Children.Add(mark);
        }
        Arrow("▲", 1, 0);
        Arrow("◀", 0, 1);
        Arrow("▶", 2, 1);
        Arrow("▼", 1, 2);
        Grid.SetColumn(inside, 1);
        Grid.SetRow(inside, 1);
        grid.Children.Add(inside);

        var label = Caption($"{zone.Display} · {rows.Count}");
        var face = new Border
        {
            CornerRadius = new CornerRadius(AgentWindow.Size("PanelRadius")),
            BorderThickness = new Thickness(2), Padding = new Thickness(12, 8),
            Child = new StackPanel { Spacing = 10, Children = { grid, label } },
        };
        return new Part(zone, rows, face, label, face);
    }

    static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("SmallSize"), FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };

    // ---- lighting one part -------------------------------------------------

    /// <summary>Light one part, or none. The lit part carries a mark and is
    /// named in the sentence above the picture, never the border colour
    /// alone.</summary>
    internal void Highlight(string? zone)
    {
        if (_lit is not null && _parts.TryGetValue(_lit, out var was)) was.Lit = false;
        // A part that is not drawn cannot be lit. Recording it as lit anyway
        // would have this quietly claim to be showing something it is not, and
        // the option's own words are already carrying where it lands.
        _lit = zone is not null && _parts.ContainsKey(zone) ? zone : null;
        if (_lit is not null) _parts[_lit].Lit = true;
    }

    /// <summary>Test seam: what one part says when it is read aloud.</summary>
    internal string TextOf(string zone) => _parts.TryGetValue(zone, out var part) ? part.Words : "";

    internal bool IsLit(string zone) => _lit == zone;

    /// <summary>One part of the device: a face that gets painted, a caption
    /// that gets marked, and whatever else is drawn with it.</summary>
    sealed class Part : Border
    {
        readonly Border _face;
        readonly TextBlock _label;
        readonly string _caption;

        internal string Id { get; }
        internal string Words { get; }

        internal Part(MainWindow.Zone zone, IReadOnlyList<Placed> rows,
                      Border face, TextBlock label, Control drawn)
        {
            Id = zone.Id;
            _face = face;
            _label = label;
            _caption = label.Text ?? "";
            Background = Brushes.Transparent;
            Child = drawn;
            Words = rows.Count == 0
                ? $"{zone.Title}: nothing here"
                : $"{zone.Title}: {rows.Count} control{(rows.Count == 1 ? "" : "s")}, "
                  + string.Join(", ", rows.Select(r => r.Name));
            AutomationProperties.SetName(this, Words);
            Lit = false;
        }

        internal bool Lit
        {
            set
            {
                AgentWindow.Paint(_face, value ? "SelectionTintBrush" : "SurfaceBrush",
                                  value ? "AccentBrush" : "SurfaceBorderBrush");
                // The mark is a second signal. Anyone who cannot see the border
                // still gets the part named in the heading above the picture.
                _label.Text = value ? "▸ " + _caption : _caption;
            }
        }
    }
}

/// <summary>The device with a sentence above it: a walkthrough of what was
/// worked out, and then the questions, asked over the same picture.</summary>
internal sealed class AgentGuide : Grid
{
    readonly DeviceMap _map;
    readonly StackPanel _above;
    readonly ScrollViewer _reading;
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

    /// <summary>Preview seam: jump to one step. False when there is no such
    /// step, so a render loop knows where the walkthrough ends.</summary>
    internal bool StepForPreview(int step)
    {
        if (step >= _steps.Count) return false;
        Go(step);
        return true;
    }

    internal AgentGuide(string game, IReadOnlyList<Placed> rows,
                        IReadOnlyList<Placed> open, IReadOnlyList<Placed> left)
    {
        _open = open;
        _left = left;
        var asking = open.Count;
        _above = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        _below = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        _map = new DeviceMap(rows);

        var bound = rows.Count(r => !r.Asking);
        var opening = $"{bound} control{(bound == 1 ? "" : "s")} worked out from the "
                    + "profiles you have already built.";
        if (asking > 0)
            opening += $" {asking} still need{(asking == 1 ? "s" : "")} you.";
        if (left.Count > 0)
            opening += $" {left.Count} {(left.Count == 1 ? "is" : "are")} left unbound on purpose.";
        // Somebody may be meeting their own device here for the first time, so
        // the first step names its parts before it counts anything on them.
        _steps.Add(new Step(null, $"{game}, on your QuadStick", opening
            + " Below is your device: three holes you sip or puff on, the lip switch under "
            + "them, the side tube beside them, and the whole mouthpiece moving as a joystick. "
            + "Each part shows how many controls landed on it.",
            rows));

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

        // Three bands: the words on top, the device under them, the buttons at
        // the bottom. The words take what they need and no more, and are capped
        // short of half the height, so a part with twelve controls on it scrolls
        // its own list instead of pushing the device off the bottom. The device
        // is on screen at every step, which is the only reason any of this is
        // worth drawing.
        RowDefinitions = new RowDefinitions("Auto,*,Auto");
        _reading = new ScrollViewer
        {
            Content = _above,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12),
        };
        // The words never take more than a third of the height, and never so
        // much that the device is squeezed below the size its part labels stop
        // being readable at. They scroll; the device does not.
        SizeChanged += (_, e) => _reading.MaxHeight =
            Math.Max(96, Math.Min(e.NewSize.Height * 0.40, e.NewSize.Height - 300));
        Add(_reading, 0);
        // The figure shrinks to fit the room it is given rather than being cut
        // off by it. Half a QuadStick is worse than a small one: somebody
        // checking their own profile has to be able to see all of the device.
        // Shrinking is better than clipping: on a short window the whole
        // device is still there, smaller. What it must never do is lose a part
        // off the edge, because somebody is checking their profile against it.
        Add(new Viewbox
        {
            Child = _map, Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
        }, 1);
        Add(_below, 2);
        Go(0);
    }

    void Add(Control child, int row)
    {
        Grid.SetRow(child, row);
        Children.Add(child);
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
        if (now.Zone is not null) _above.Children.Add(Bindings(now.Rows));
        // The last step is where what is NOT being bound gets said, by name and
        // with its reason. A control left on purpose and a control nobody got
        // to are not the same thing, and neither of them is allowed to be a
        // number somebody has to go looking behind.
        if (_at == _steps.Count - 1)
        {
            foreach (var open in _open) _above.Children.Add(Aside(open.Name, open.Why));
            if (_left.Count > 0)
            {
                _above.Children.Add(Body($"{_left.Count} left unbound on purpose:"));
                // Thirteen keyboard keys left alone for the same reason is one
                // fact, not thirteen. Listing it thirteen times buried the four
                // questions above it that actually wanted an answer.
                foreach (var same in _left.GroupBy(l => l.Why))
                    _above.Children.Add(Aside(string.Join(", ", same.Select(l => l.Name)), same.Key));
            }
        }
        _above.Children.Add(new TextBlock
        {
            Text = $"Step {_at + 1} of {_steps.Count}",
            FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
            Margin = new Thickness(0, 4, 0, 0),
        });
        _reading.Offset = default;

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

    /// <summary>What landed on one part, gathered by the thing you do to fire
    /// it. Six rows all reading "up" is one thing you do with your mouth and
    /// six outputs it sends, and reading it as six separate bindings is how
    /// twelve joystick rows became unreadable.</summary>
    static Control Bindings(IReadOnlyList<Placed> rows)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var at = 0;
        foreach (var group in rows.GroupBy(r => r.Trigger))
        {
            var first = true;
            foreach (var row in group)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (first)
                {
                    var trigger = new TextBlock
                    {
                        Text = group.Key, FontSize = AgentWindow.Size("BodySize"),
                        FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 190, Margin = new Thickness(0, 6, 16, 0),
                    };
                    Grid.SetRow(trigger, at);
                    grid.Children.Add(trigger);
                    first = false;
                }
                var said = new StackPanel { Spacing = 1, Margin = new Thickness(0, 6, 0, 0) };
                said.Children.Add(new TextBlock
                {
                    Text = row.Behaviour.Length > 0 ? $"{row.Name} ({Lower(row.Behaviour)})" : row.Name,
                    FontSize = AgentWindow.Size("BodySize"), TextWrapping = TextWrapping.Wrap,
                });
                var why = Short(row.Why);
                if (why.Length > 0)
                    said.Children.Add(new TextBlock
                    {
                        Text = why, FontSize = AgentWindow.Size("SmallSize"),
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    });
                AutomationProperties.SetName(said, $"{row.Name}, {group.Key}. {row.Behaviour} {row.Why}");
                Grid.SetRow(said, at);
                Grid.SetColumn(said, 1);
                grid.Children.Add(said);
                at++;
            }
        }
        return grid;
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
        _reading.Offset = default;

        _below.Children.Clear();
        var all = new List<Button>();
        for (int n = 0; n < options.Count; n++)
        {
            var option = options[n];
            var choice = n;
            var where = option.Inputs.Count > 0
                ? $"{option.Trigger} on the {Title(option.Zone)}, {Lower(MainWindow.FunctionExplain(option.Function))}"
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
            Margin = new Thickness(0, 8, 0, 0),
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

    // ---- words ------------------------------------------------------------

    /// <summary>A device output said in English. The chart's own word for a
    /// control is always better and is used when there is one; this is for the
    /// rest, because "kb_escape" on a walkthrough of somebody's device tells
    /// them nothing they did not already have to know.</summary>
    internal static string Speak(string output) => output switch
    {
        "" => "",
        _ when output.StartsWith("kb_", StringComparison.Ordinal) =>
            Sentence(output[3..]) + " key",
        _ when output.StartsWith("mouse_", StringComparison.Ordinal)
            && output.EndsWith("_button", StringComparison.Ordinal) =>
            Sentence(output[6..^7]) + " mouse button",
        _ when output.StartsWith("dpad_", StringComparison.Ordinal) =>
            "D-pad " + output[5..].ToLowerInvariant(),
        _ => Sentence(output.Replace("_joy_", " stick ")),
    };

    static string Sentence(string token)
    {
        var said = token.Replace('_', ' ').Trim();
        return said.Length == 0 ? "" : char.ToUpperInvariant(said[0]) + said[1..];
    }

    static string Lower(string said) =>
        said.Length == 0 ? "" : char.ToLowerInvariant(said[0]) + said[1..].TrimEnd('.');

    /// <summary>The evidence, down to the part that is about this profile. The
    /// tail naming the exact file and row it was copied from is kept whole in
    /// the step list and in the approval, which is where somebody checking the
    /// work goes; here it pushed the device off the screen.</summary>
    static string Short(string why) => why.Split(';')[0].Trim();

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
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 8, 0, 0) };
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
