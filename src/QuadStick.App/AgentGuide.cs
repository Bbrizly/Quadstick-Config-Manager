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
// The device is the biggest thing on the screen and never scrolls away. A
// heading sits above it, what landed on this part sits under it, and the way
// forward sits under that. One column, centred, capped at a line length
// somebody can actually read.
//
// Every word here is rationed. The first cut of this put a paragraph of device
// manual and a line of evidence under every row, which is how eleven joystick
// bindings became a wall of grey text with the QuadStick squeezed underneath
// it. The evidence is still in the steps view and in the list being approved,
// which is where somebody checking the work goes.
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
        Padding = new Thickness(20, 18);
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
    readonly StackPanel _head;
    readonly StackPanel _body;
    readonly ScrollViewer _reading;
    readonly Grid _foot;
    readonly List<Step> _steps = new();
    readonly IReadOnlyList<Placed> _open;
    readonly IReadOnlyList<Placed> _left;
    int _at = -1;

    // A line long enough to read comfortably and no longer. Text run edge to
    // edge across the window is the other half of why this was unreadable.
    const double Column = 620;

    sealed record Step(string? Zone, string Heading, string Under, IReadOnlyList<Placed> Rows);

    /// <summary>The walkthrough ran out. Whatever comes next, a question or the
    /// approval, happens now.</summary>
    internal Action? Walked { get; set; }

    /// <summary>Still stepping through parts. A question that arrives while
    /// this is true waits: interrupting somebody halfway through being shown
    /// their own device is how you get an answer they did not think about.</summary>
    internal bool Walking => _at >= 0 && _at < _steps.Count;

    /// <summary>Test seam: everything this step says in words right now.</summary>
    internal string Saying =>
        string.Join("\n", Texts(_head).Concat(Texts(_body)).Concat(Texts(_foot)));

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
        _head = new StackPanel
        {
            Spacing = 4, MaxWidth = Column,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _body = new StackPanel
        {
            Spacing = 6, MaxWidth = Column,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _map = new DeviceMap(rows);

        var bound = rows.Count(r => !r.Asking);
        var counts = $"{bound} control{(bound == 1 ? "" : "s")} worked out";
        if (asking > 0) counts += $", {asking} still need{(asking == 1 ? "s" : "")} you";
        if (left.Count > 0)
            counts += $", {left.Count} {(left.Count == 1 ? "is" : "are")} left unbound on purpose";
        // Three facts and a picture. The old opening spent four lines naming the
        // parts of a QuadStick, which the drawing right underneath it labels.
        _steps.Add(new Step(null, $"{game}, on your QuadStick", counts + ".", rows));

        foreach (var id in _map.Parts)
        {
            var zone = MainWindow.AllZones.First(z => z.Id == id);
            var here = rows.Where(r => r.Zone == id).ToList();
            if (here.Count == 0) continue;
            // One sentence of what this part is, not the paragraph. The count is
            // already on the part itself, and what landed there is right below.
            _steps.Add(new Step(id, zone.Title, First(zone.Blurb), here));
        }

        _steps.Add(new Step(null,
            asking > 0 ? $"{asking} thing{(asking == 1 ? "" : "s")} still need you"
                       : "That is the whole profile",
            asking > 0
                ? "Your own profiles answer these both ways, so they are yours to call."
                : "Nothing here needs you. Nothing is written until you approve it.",
            Array.Empty<Placed>()));

        // Four bands: the heading, the device, what landed on it, the way
        // forward. The device gets the star row, so it takes everything the
        // other three do not, and it is the biggest thing on the screen at
        // every step. That is the only reason any of this is worth drawing.
        RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto");
        _head.Margin = new Thickness(0, 0, 0, 10);
        Add(_head, 0);
        // It scales to the room it is given rather than being cut off by it, up
        // as well as down. Half a QuadStick is worse than a small one: somebody
        // checking their own profile has to see all of the device.
        Add(new Viewbox
        {
            Child = _map, Stretch = Stretch.Uniform, MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
        }, 1);
        _reading = new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 14, 0, 0),
        };
        // What landed on this part never takes more than about a third of the
        // height. A part with twelve controls scrolls its own list instead of
        // squeezing the device out of the window.
        SizeChanged += (_, e) => _reading.MaxHeight =
            Math.Max(120, Math.Min(e.NewSize.Height * 0.36, e.NewSize.Height - 280));
        Add(_reading, 2);
        _foot = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            Margin = new Thickness(0, 16, 0, 0),
        };
        Add(_foot, 3);
        Go(0);
    }

    void Add(Control child, int row)
    {
        Grid.SetRow(child, row);
        Children.Add(child);
    }

    /// <summary>The first sentence of a longer blurb. What a part is takes one
    /// line here; the rest of it is the device manual, and the editor is where
    /// that belongs.</summary>
    static string First(string blurb)
    {
        var stop = blurb.IndexOf(". ", StringComparison.Ordinal);
        return stop < 0 ? blurb : blurb[..(stop + 1)];
    }

    // ---- the walkthrough --------------------------------------------------

    void Go(int step)
    {
        _at = Math.Clamp(step, 0, _steps.Count - 1);
        var now = _steps[_at];
        _map.Highlight(now.Zone);

        _head.Children.Clear();
        _head.Children.Add(Heading(now.Heading));
        _head.Children.Add(Under(now.Under));

        _body.Children.Clear();
        // Every control on this part, in the game's words, gathered by what
        // fires it. The opening step has no part, so it stays a count rather
        // than listing the whole profile before they have seen the device.
        if (now.Zone is not null) _body.Children.Add(Bindings(now.Rows));
        // The last step is where what is NOT being bound gets said, by name and
        // with its reason. A control left on purpose and a control nobody got
        // to are not the same thing, and neither is allowed to be a number
        // somebody has to go looking behind.
        if (_at == _steps.Count - 1)
        {
            if (_open.Count > 0)
                _body.Children.Add(Body(string.Join(", ", _open.Select(o => o.Name)) + "."));
            if (_left.Count > 0)
            {
                _body.Children.Add(Body($"{_left.Count} left unbound on purpose:"));
                // Thirteen keyboard keys left alone for the same reason is one
                // fact, not thirteen. Listing it thirteen times buried the four
                // questions above it that actually wanted an answer.
                foreach (var same in _left.GroupBy(l => l.Why))
                    _body.Children.Add(Aside(string.Join(", ", same.Select(l => l.Name)), same.Key));
            }
        }
        _reading.Offset = default;

        var last = _at == _steps.Count - 1;
        Foot(
            left: last ? null : Button("Skip", () =>
            {
                _at = _steps.Count;
                _map.Highlight(null);
                Walked?.Invoke();
            }, quiet: true, tell: "Skip the walkthrough and go straight to the questions"),
            middle: Dots(),
            back: _at > 0 ? Button("Back", () => Go(_at - 1)) : null,
            next: Button(last ? "Continue" : "Next", () =>
            {
                if (last) { _at = _steps.Count; Walked?.Invoke(); }
                else Go(_at + 1);
            }, primary: true));
    }

    /// <summary>How far along, as one dot per step and as a sentence. The dots
    /// are the glance; the sentence is what gets read aloud and what anyone who
    /// cannot pick the filled dot out of the row still has.</summary>
    Control Dots()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        for (int n = 0; n < _steps.Count; n++)
        {
            var here = n == _at;
            // The step you are on is a bar, the rest are dots. Shape carries it,
            // so the fill is the second signal and never the only one.
            var dot = new Border
            {
                Width = here ? 20 : 7, Height = 7, CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions
                .DynamicResourceExtension(here ? "AccentBrush" : "SurfaceBorderBrush");
            row.Children.Add(dot);
        }
        var said = new TextBlock
        {
            Text = $"Step {_at + 1} of {_steps.Count}",
            FontSize = AgentWindow.Size("SmallSize"), Classes = { "muted" },
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel
        {
            Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { row, said },
        };
        AutomationProperties.SetName(stack, $"Step {_at + 1} of {_steps.Count}");
        return stack;
    }

    /// <summary>The bar under everything: the way out on the left, where you are
    /// in the middle, the way on at the right. Same three places at every step,
    /// so nothing under the hand moves between them.</summary>
    void Foot(Control? left, Control? middle, Control? back, Control? next)
    {
        _foot.Children.Clear();
        void Put(Control? child, int column, HorizontalAlignment where)
        {
            if (child is null) return;
            child.HorizontalAlignment = where;
            Grid.SetColumn(child, column);
            _foot.Children.Add(child);
        }
        Put(left, 0, HorizontalAlignment.Left);
        Put(middle, 1, HorizontalAlignment.Center);
        var onward = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (back is not null) onward.Children.Add(back);
        if (next is not null) onward.Children.Add(next);
        Put(onward, 2, HorizontalAlignment.Right);
    }

    /// <summary>What landed on one part, one line per thing you do with your
    /// mouth. Eleven joystick rows are four mouth movements and what each of
    /// them sends, and drawing it as eleven rows with a line of evidence under
    /// every one is how this became a wall of text.</summary>
    static Control Bindings(IReadOnlyList<Placed> rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var at = 0;
        foreach (var group in rows.GroupBy(r => r.Trigger))
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var trigger = new TextBlock
            {
                Text = group.Key, FontSize = AgentWindow.Size("BodySize"),
                FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 170, Margin = new Thickness(0, 5, 16, 0),
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetRow(trigger, at);
            grid.Children.Add(trigger);

            var names = string.Join(", ", group.Select(Named));
            var said = new TextBlock
            {
                Text = names, FontSize = AgentWindow.Size("BodySize"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0),
            };
            // The evidence for every row is read aloud with it, and is in full
            // in the steps view and on the list being approved. On screen here
            // it doubled the height of every part for a line nobody was reading.
            AutomationProperties.SetName(said, $"{group.Key}: {names}. "
                + string.Join(" ", group.Select(r => r.Why)));
            Grid.SetRow(said, at);
            Grid.SetColumn(said, 1);
            grid.Children.Add(said);
            at++;
        }
        return grid;
    }

    static string Named(Placed row) =>
        row.Behaviour.Length > 0 ? $"{row.Name} ({Lower(row.Behaviour)})" : row.Name;

    // ---- a question, asked over the same picture --------------------------

    /// <summary>One question, above the device, with each option lighting the
    /// part it would land on as it is reached. Reached by keyboard as well as
    /// by mouse: focus lights the part, so tabbing through the options walks
    /// the device.</summary>
    internal void Ask(string question, string about, IReadOnlyList<Placed> options,
                      Action<int> answer)
    {
        _map.Highlight(null);
        _head.Children.Clear();
        _head.Children.Add(Heading(question));
        _head.Children.Add(Under(about));
        _reading.Offset = default;
        _foot.Children.Clear();

        _body.Children.Clear();
        var choices = new StackPanel { Spacing = 8 };
        _body.Children.Add(choices);
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
            choices.Children.Add(button);
        }
    }

    /// <summary>The question is answered. Say what was chosen, over the part it
    /// landed on, and stop offering to change it here.</summary>
    internal void Chose(string what, Placed? landed)
    {
        _map.Highlight(landed is { Inputs.Count: > 0 } ? landed.Zone : null);
        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            Text = $"You chose: {what}", FontSize = AgentWindow.Size("BodySize"),
            FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });
    }

    /// <summary>Nothing left to ask here. Says so rather than sitting on the
    /// last thing that happened, which reads as a run that stopped.</summary>
    internal void Waiting(string text, string? under = null)
    {
        _map.Highlight(null);
        _head.Children.Clear();
        _head.Children.Add(Heading(text));
        if (under is { Length: > 0 }) _head.Children.Add(Under(under));
        _body.Children.Clear();
        _foot.Children.Clear();
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

    static string Title(string zone) =>
        MainWindow.AllZones.FirstOrDefault(z => z.Id == zone)?.Title.ToLowerInvariant() ?? zone;

    static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("SectionSize"), FontWeight = FontWeight.Bold,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };

    /// <summary>The one line under the heading. One, not a paragraph.</summary>
    static TextBlock Under(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("BodySize"), Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
    };

    static TextBlock Body(string text) => new()
    {
        Text = text, FontSize = AgentWindow.Size("BodySize"), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A control that is not being bound: what it is, and why not.</summary>
    static Control Aside(string name, string why)
    {
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(0, 6, 0, 0) };
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

    static Button Button(string text, Action clicked, bool primary = false, bool quiet = false,
                         string? tell = null)
    {
        var button = new Button
        {
            Content = text, MinWidth = quiet ? 88 : 120,
            MinHeight = AgentWindow.Size("ControlHeight"),
        };
        if (primary) button.Classes.Add("primary");
        if (quiet) button.Classes.Add("quiet");
        if (tell is not null) AutomationProperties.SetName(button, tell);
        button.Click += (_, _) => clicked();
        return button;
    }
}
