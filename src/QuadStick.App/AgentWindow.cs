using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace QuadStick.App;

// Name a game. It reads how that game is controlled, translates it into the
// control scheme this person already spent years learning, asks about whatever
// their own profiles cannot settle, and writes nothing until they say so.
//
// Two views of one run, and both of them matter.
//
// Every step the agent takes is a card here, in the order it happened, with
// what it was given and what came back. A profile arriving fully formed with no
// account of where each row came from is exactly the thing a person cannot
// check, and this is a file they steer their computer with.
//
// But a correct account nobody can read is not an account. So before anybody is
// asked anything, the same profile is drawn on their own device and walked
// through part by part, in the game's own words, and the questions are asked
// over that picture. The steps are still one button away, and the approval at
// the end is still over the list.
//
// Nothing here writes a cell. The agent process does that, behind the same
// refusals the terminal path uses, so there is one write path and not two.
public class AgentWindow : Window
{
    readonly MainWindow _owner;
    readonly string _root;

    readonly TextBox _ask;
    readonly Button _go;
    readonly Button _stop;
    readonly Button _close;
    readonly StackPanel _stream;
    readonly ScrollViewer _scroll;
    readonly TextBlock _status;
    readonly TextBlock _explain;
    readonly StackPanel _setup;
    readonly CheckBox _replay;
    readonly Dictionary<string, ToolCard> _cards = new();
    readonly List<ToolCard> _timed = new();
    readonly DispatcherTimer _tick;
    DateTime _began;
    int _asked, _recorded;

    // The guide: the profile drawn on the device, walked through before anyone
    // is asked anything, and the questions asked over the same picture.
    readonly ContentControl _guideHost;
    readonly StackPanel _switch;
    readonly Button _toGuide;
    readonly Button _toSteps;
    AgentGuide? _guide;
    // Anything that arrived while somebody was still being walked through their
    // own device. The run is blocked on stdin either way, so nothing is lost by
    // making it wait; interrupting the walkthrough would cost an answer given
    // without the context it was about to get.
    readonly Queue<AgentEvent> _held = new();

    IAgentRun? _bridge;
    string? _written;
    bool _running;
    // Whether the run has already accounted for itself. Sniffing the status
    // line for this got it wrong the moment the wording changed, and getting it
    // wrong means a run that died in silence stays silent.
    bool _spoke;

    /// <summary>Test seam: what a finished run does with the profile it wrote.
    /// The app opens it in the editor; a test just records the path.</summary>
    internal Action<string> OpenWritten { get; set; }

    /// <summary>Test seam: opening it and going straight on to install it.</summary>
    internal Action<string>? InstallWritten { get; set; }

    /// <summary>Test seam: whether a QuadStick is plugged in.</summary>
    internal Func<bool> DeviceConnected { get; set; } = MainWindow.DeviceIsConnected;

    /// <summary>Test seam: how the agent process gets started. Tests hand this
    /// a bridge over a scripted stream so no model and no network are involved.</summary>
    internal Func<IReadOnlyList<string>, IAgentRun>? StartWith { get; set; }

    readonly bool _changing;

    public AgentWindow(MainWindow owner, bool changing = false)
        : this(owner, AgentBridge.FindAgentRoot(), changing) { }

    internal AgentWindow(MainWindow owner, string? root, bool changing = false)
    {
        Classes.Add("dialog");
        _owner = owner;
        _root = root ?? "";
        _changing = changing;
        OpenWritten = path => _owner.OpenPath(path);
        InstallWritten = path => _ = _owner.OpenPathAndInstallAsync(path);
        Title = changing ? "Ask for a change" : "Set up a game";
        Width = Math.Min(820 * owner.UiScale, 1100);
        Height = Math.Min(760 * owner.UiScale, 940);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = changing ? "Ask for a change" : "Set up a game",
            FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
        };
        _explain = new TextBlock
        {
            Text = changing
                ? "Say what you want changed, in your own words. It finds the row you mean, "
                + "changes only that row, and shows you the exact cell it would change. "
                + "Nothing is changed until you say so."
                : "Name any game or app. It reads how that game is controlled, then works out "
                + "what each control should be on your QuadStick from the profiles you have "
                + "already built. It asks you about anything your own profiles do not settle, "
                + "and it writes nothing until you say so.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _ask = new TextBox
        {
            Watermark = changing ? "make sprint a hard puff instead"
                                 : "Hollow Knight Silksong",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_ask, changing ? "The change you want made"
                                                    : "The game to set up");
        _ask.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Begin(); } };

        _go = new Button
        {
            Content = changing ? "Work it out" : "Set it up",
            Classes = { "primary" }, MinWidth = 130,
        };
        AutomationProperties.SetName(_go, changing ? "Work out what to change"
                                                  : "Start setting up the game you named");
        _go.Click += (_, _) => Begin();

        // A run can sit in a model call for a while. Without this the only way
        // out is closing the window, and somebody halfway through answering
        // should not have to throw the whole transcript away to stop it.
        _stop = new Button { Content = "Stop", MinWidth = 100, IsEnabled = false };
        AutomationProperties.SetName(_stop, "Stop the run. Nothing that has not been written stays.");
        _stop.Click += (_, _) =>
        {
            _stop.IsEnabled = false;
            Say("Stopping...");
            _bridge?.Dispose();
        };

        // Either it asks, or it replays. There is no quiet third state that
        // reuses an old answer without saying so: a run that reused one and
        // finished in a second is exactly what makes people ask whether any of
        // it happened, and the answer has to be on screen before it starts.
        _replay = new CheckBox { Content = "From the recording, no internet", IsChecked = false };
        AutomationProperties.SetName(_replay,
            "Run from recorded answers instead of asking the model. Needs no internet. "
            + "Unticked, every step asks the model.");

        _stream = new StackPanel { Spacing = 10 };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _stream,
        };
        AutomationProperties.SetName(_scroll, "What the agent has done so far");

        _status = new TextBlock
        {
            Text = _root.Length == 0
                ? "The agent files are not next to this app, so nothing can run."
                : changing && owner.CurrentProfilePath is null
                    ? "Save this profile first. A change is made to the file on disk, so there has to be one."
                    : "",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _status.IsVisible = _status.Text!.Length > 0;

        _close = new Button { Content = "Close", MinWidth = 130, IsCancel = true };
        AutomationProperties.SetName(_close, "Close this window");
        _close.Click += (_, _) => Close();

        _setup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _setup.Children.Add(_go);
        _setup.Children.Add(_replay);

        // Stop sits with Close, not with Set it up. They are the two ways out
        // of this window, and keeping Stop up top cost a whole row of height
        // above the device for a button that is only ever pressed to leave.
        // Ruled off, because the guide has its own row of buttons directly above
        // it. Without the line, Skip and Close read as one bank of four and the
        // way out of the window looks like a step of the walkthrough.
        var out_ = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            [!BorderBrushProperty] = new DynamicResourceExtension("SurfaceBorderBrush"),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children = { _close, _stop },
            },
        };

        // Two ways to look at the same run: the device with what landed on it,
        // and every step it took to get there. Neither replaces the other. The
        // guide is what a person checks the profile with; the steps are what
        // they check the guide with.
        // One track, two keys, like the view switch in the editor. Disabling the
        // key you are on read as a dead grey button; the fill on the active one
        // is what says which view this is, and the track is what says the two
        // are the same choice.
        _guideHost = new ContentControl { IsVisible = false };
        _toGuide = new Button { Content = "Your QuadStick", MinWidth = 150, Padding = new Thickness(14, 0),
            FontWeight = FontWeight.SemiBold, Classes = { "switchkey" } };
        AutomationProperties.SetName(_toGuide,
            "Show the profile drawn on your QuadStick, part by part");
        _toGuide.Click += (_, _) => Look(guide: true);
        _toSteps = new Button { Content = "What it did", MinWidth = 150, Padding = new Thickness(14, 0),
            FontWeight = FontWeight.SemiBold, Classes = { "switchkey" } };
        AutomationProperties.SetName(_toSteps, "Show every step the agent took, in order");
        _toSteps.Click += (_, _) => Look(guide: false);
        _switch = new StackPanel
        {
            Orientation = Orientation.Horizontal, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Border
                {
                    Classes = { "switchtrack" },
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 2,
                        Children = { _toGuide, _toSteps },
                    },
                },
            },
        };

        var views = new Grid();
        views.Children.Add(_scroll);
        views.Children.Add(_guideHost);

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        foreach (var (control, dock, margin) in new (Control, Dock, Thickness)[]
        {
            (_explain, Dock.Top, new Thickness(0, 0, 0, 12)),
            (_ask, Dock.Top, new Thickness(0, 0, 0, 10)),
            (_setup, Dock.Top, new Thickness(0, 0, 0, 14)),
            (_switch, Dock.Top, new Thickness(0, 0, 0, 12)),
            (out_, Dock.Bottom, new Thickness(0, 12, 0, 0)),
            (_status, Dock.Bottom, new Thickness(0, 12, 0, 0)),
        })
        {
            control.Margin = margin;
            DockPanel.SetDock(control, dock);
            panel.Children.Add(control);
        }
        panel.Children.Add(views);

        // One timer for the whole window, not one per card. A step that sits in
        // a model call for three minutes has to visibly still be working, or
        // there is no way to tell it apart from a run that died.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => TickNow();

        Content = MainWindow.ZoomWrap(panel, owner.UiScale);
        Opened += (_, _) => _ask.Focus();
        Empty();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing the window closes the agent's pipe, which stops it at its next
        // question rather than leaving it to finish a job nobody is watching.
        _bridge?.Dispose();
        base.OnClosed(e);
    }

    // ---- starting a run ---------------------------------------------------

    void Empty()
    {
        _stream.Children.Clear();
        _cards.Clear();
        _stream.Children.Add(new TextBlock
        {
            Text = "Nothing has run yet. Name a game above and press Set it up.",
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });
    }

    /// <summary>What the person typed, as arguments. A sentence about a profile
    /// that is already open is a change to it; anything else names a game.</summary>
    internal IReadOnlyList<string> Arguments(string said, string? openProfile, bool replay,
                                             bool changing = false)
    {
        var words = said.Trim();
        var list = new List<string>();
        if (openProfile is not null && (changing || LooksLikeAChange(words)))
        {
            list.Add("--edit"); list.Add(openProfile);
            list.Add("--request"); list.Add(words);
        }
        else
        {
            list.Add("--game"); list.Add(words);
        }
        // --live, not the default, on purpose. The default reuses a recorded
        // answer wherever one exists, which from in here is indistinguishable
        // from the run having made it up. In the window it either asks or it
        // replays, and it says which.
        list.Add(replay ? "--replay" : "--live");
        return list;
    }

    /// <summary>A request to change something, rather than a game's name. Verbs
    /// only, and only when a profile is actually open: guessing wrong in the
    /// other direction would silently edit a file they meant to leave alone.</summary>
    internal static bool LooksLikeAChange(string said)
    {
        var first = said.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return new[] { "make", "change", "swap", "move", "set", "bind", "rebind", "remap", "put" }
            .Contains(first.ToLowerInvariant());
    }

    void Begin()
    {
        if (_running) return;
        var said = (_ask.Text ?? "").Trim();
        if (said.Length == 0)
        {
            Say(_changing ? "Say what you want changed first."
                          : "Type a game first, then press Set it up.");
            _ask.Focus();
            return;
        }
        if (_changing && _owner.CurrentProfilePath is null)
        {
            Say("Save this profile first. A change is made to the file on disk, so there has to be one.");
            return;
        }
        if (_root.Length == 0 && StartWith is null)
        {
            Say("The agent files are not next to this app, so nothing can run.");
            return;
        }

        // A follow-up is a change to the profile this run just wrote, not a new
        // game. That is the whole of "talk to it once it is done": the same box,
        // pointed at what is now on disk.
        var target = _changing ? _owner.CurrentProfilePath : _written;

        // What this window is for is worth a paragraph before the first run and
        // nothing after it. Once something is on screen, that paragraph is
        // taking room from the thing it was explaining.
        _explain.IsVisible = false;

        _stream.Children.Clear();
        _cards.Clear();
        _timed.Clear();
        _held.Clear();
        _guide = null;
        _guideHost.Content = null;
        _switch.IsVisible = false;
        Look(guide: false);
        _written = null;
        _spoke = false;
        _running = true;
        _asked = _recorded = 0;
        _began = DateTime.UtcNow;
        _tick.Start();
        _go.IsEnabled = false;
        _stop.IsEnabled = true;
        _ask.IsEnabled = false;
        Say(_changing ? $"Working out what \"{said}\" means..." : $"Setting up {said}...");

        var arguments = Arguments(said, target, _replay.IsChecked == true,
                                  _changing || target is not null);
        try
        {
            _bridge = StartWith is not null ? StartWith(arguments)
                                            : new AgentBridge(_root, arguments);
            _bridge.Event += e => Dispatcher.UIThread.Post(() => Show(e));
            _bridge.Trouble += t => Dispatcher.UIThread.Post(() => Note(t, muted: true));
            _bridge.Ended += code => Dispatcher.UIThread.Post(() => Finished(code));
            _bridge.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _running = false;
            _go.IsEnabled = true;
            _stop.IsEnabled = false;
            _ask.IsEnabled = true;
            Say($"The agent could not be started: {ex.Message}");
        }
    }

    /// <summary>Move every card that is still working on one second. Public to
    /// the tests so the ticking can be driven without waiting on a real clock.</summary>
    internal void TickNow()
    {
        var now = DateTime.UtcNow;
        var working = false;
        foreach (var card in _timed)
        {
            if (!card.Running) continue;
            working = true;
            card.Tick((now - card.Began).TotalSeconds);
        }
        // A step that is still going says so on the status line too, so the
        // count of seconds is readable without hunting for the last card.
        if (working && _running)
            Say($"{TotalSeconds:0}s so far. {_asked} asked of the model, "
                + $"{_recorded} replayed from the recording.");
    }

    double TotalSeconds => (DateTime.UtcNow - _began).TotalSeconds;

    void Finished(int code)
    {
        _running = false;
        _tick.Stop();
        // A card left mid-step when the run ended must stop claiming to work.
        foreach (var card in _timed) card.Abandon();
        _go.IsEnabled = true;
        _stop.IsEnabled = false;
        _ask.IsEnabled = true;
        // A run that ends without having said why is the one thing this window
        // must never do quietly, so the exit code is turned into a sentence.
        if (_spoke) return;
        Say(code != 0
            ? "The run stopped before it finished, and nothing was written."
            : "The run finished without writing anything.");
    }

    // An empty status takes its row with it. The row was costing height above
    // the device to hold nothing, or to repeat what the guide's own heading
    // already said.
    void Say(string message)
    {
        _status.Text = message;
        _status.IsVisible = message.Length > 0;
    }

    // ---- turning events into cards ---------------------------------------

    void Show(AgentEvent e)
    {
        // A card that cannot be drawn must not become silence. The app's crash
        // guard handles a broken click handler by swallowing it, which here
        // would mean the agent kept working while the window showed nothing.
        try { Draw(e); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException
                                     or JsonException or NullReferenceException)
        {
            Note($"This step could not be drawn ({ex.Message}). What it said was: {e.Raw}",
                 muted: true);
        }
        // Follow the run. The last card is the one that matters, and on a long
        // run the interesting part is always at the bottom.
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    void Draw(AgentEvent e)
    {
        switch (e.Kind)
        {
            case "run": Mode(e); break;
            case "stage": Stage(e); break;
            case "tool": Add(new ToolCard(e), e.Id); break;
            case "tool_done": Done(e); break;
            case "note": Note(e.Text); break;
            case "tally": Tally(e); break;
            case "rows": Rows(e); break;
            case "map": Map(e); break;
            case "question": Question(e); break;
            case "confirm": Confirm(e); break;
            // Both of these are the run accounting for itself, which is what
            // stops the ending from being narrated twice.
            case "done": _spoke = true; Written(e); break;
            case "failed": _spoke = true; Failed(e); break;
            // Anything this version does not recognise is still something the
            // agent said. Dropping it would be the window quietly deciding
            // what the person is allowed to know.
            default: Note(e.Raw, muted: true); break;
        }
    }

    /// <summary>What this run is allowed to do, said before it does anything.
    /// A run everybody watched finish in a second needs to have said up front
    /// whether it was thinking or reading a recording.</summary>
    void Mode(AgentEvent e)
    {
        var mode = e.Str("mode");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = mode switch
            {
                "live" => "Asking the model for every binding. A game already charted is not read again.",
                "replay" => "Running from the recording. No model and no internet.",
                _ => "Asking the model, and reusing a recorded answer where there is one.",
            },
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{e.Str("model")} through {e.Str("backend")}. Every step below says which "
                 + "of the two it was.",
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });
        Add(Panel(panel, "SurfaceSubtleBrush"));
    }

    void Add(Control card, string? id = null)
    {
        _stream.Children.Add(card);
        if (card is ToolCard timed) _timed.Add(timed);
        if (id is { Length: > 0 } && card is ToolCard tool) _cards[id] = tool;
    }

    void Done(AgentEvent e)
    {
        // Counted per step, from what the run said, so the tally on screen is
        // the run's own account of itself and not the window's guess.
        if (e.Str("origin") == "live") _asked++;
        else if (e.Str("origin") == "cache") _recorded++;
        if (_cards.TryGetValue(e.Id, out var card)) card.Settle(e);
        // A result for a card nobody started is still something the agent said,
        // so it becomes its own card rather than being dropped.
        else Add(new ToolCard(e));
    }

    void Stage(AgentEvent e)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 2) };
        var rule = new Border { Height = 1 };
        rule[!BackgroundProperty] = new DynamicResourceExtension("SurfaceBorderBrush");
        panel.Children.Add(rule);
        panel.Children.Add(new TextBlock
        {
            Text = e.Title.ToUpperInvariant(), Classes = { "section" }, TextWrapping = TextWrapping.Wrap,
        });
        // Why this phase is happening at all. Watching a run without this is
        // watching a machine work: everything is visible and none of it means
        // anything, which is the complaint that put this line here.
        if (e.Str("why") is { Length: > 0 } why)
            panel.Children.Add(new TextBlock
            {
                Text = why, FontSize = Size("BodySize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
        Add(panel);
    }

    /// <summary>The shape of the whole job as one bar: what his own profiles
    /// answered, what still needs a person, and what the chart never covered.
    ///
    /// The same three numbers are in the cards below it. A number you have to
    /// assemble by reading forty cards is a number nobody has.</summary>
    void Tally(AgentEvent e)
    {
        int of = e.Num("of"), answered = e.Num("answered"), asking = e.Num("asking");
        var rest = Math.Max(0, of - answered - asking);
        var parts = new (int Count, string Word, string Brush)[]
        {
            (answered, "answered from his own profiles", "AccentBrush"),
            (asking, "the evidence cannot settle", "SurfaceBorderBrush"),
            (rest, "the chart does not cover", "SurfaceSubtleBrush"),
        }.Where(p => p.Count > 0).ToArray();

        var bar = new Grid { Height = 14 };
        var legend = new StackPanel { Spacing = 2 };
        for (int n = 0; n < parts.Length; n++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition(parts[n].Count, GridUnitType.Star));
            var block = new Border
            {
                CornerRadius = new CornerRadius(3), MinWidth = 6,
                Margin = new Thickness(n == 0 ? 0 : 2, 0, 0, 0),
                [!BackgroundProperty] = new DynamicResourceExtension(parts[n].Brush),
            };
            Grid.SetColumn(block, n);
            bar.Children.Add(block);
            // Every segment is named and counted in words underneath. The widths
            // are the second signal, never the only one.
            legend.Children.Add(new TextBlock
            {
                Text = $"{parts[n].Count}  {parts[n].Word}",
                FontSize = Size("SmallSize"), TextWrapping = TextWrapping.Wrap,
            });
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{of} control{(of == 1 ? "" : "s")} this game uses",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(bar);
        panel.Children.Add(legend);
        AutomationProperties.SetName(panel, $"{of} controls this game uses. "
            + string.Join(". ", parts.Select(p => $"{p.Count} {p.Word}")));
        Add(Panel(panel, "SurfaceSubtleBrush"));
    }

    void Note(string text, bool muted = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var block = new TextBlock
        {
            Text = text, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 0),
        };
        if (muted) block.Classes.Add("muted");
        Add(block);
    }

    void Rows(AgentEvent e)
    {
        var rows = e.List("rows");
        if (rows.Count == 0) return;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{e.Title}: {rows.Count}",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        foreach (var row in rows) panel.Children.Add(BindingLine(row));
        Add(Panel(panel, "SurfaceSubtleBrush"));
    }

    /// <summary>One binding, as words. The reason is on the line under it,
    /// because a row without its reason is a row nobody can check.</summary>
    static Control BindingLine(JsonElement row)
    {
        string Text(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        var inputs = row.TryGetProperty("inputs", out var i) && i.ValueKind == JsonValueKind.Array
            ? string.Join(" + ", i.EnumerateArray().Select(x => x.GetString())) : "";
        var output = Text("output");
        var function = Text("function");
        var why = Text("why");
        var was = Text("was");
        // The game's own word for the row, when the chart had one. This is the
        // same word that goes into column L, so the list and the file agree.
        var action = Text("action");
        var named = action.Length > 0 ? $"{action}   {output}" : output;

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{named}   {inputs}, {function}",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        if (was.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = $"was {was}",
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
        if (why.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = why, FontSize = Size("SmallSize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            });
        AutomationProperties.SetName(stack,
            $"{named} bound to {inputs}, {function}. {(was.Length > 0 ? $"Was {was}. " : "")}{why}");
        return stack;
    }

    // ---- the device, and the walk through it ------------------------------

    /// <summary>Which of the two views is on screen. The other one is still
    /// there and still one button away, so nothing is ever hidden, only put
    /// behind the thing a person is actually doing.</summary>
    void Look(bool guide)
    {
        _guideHost.IsVisible = guide;
        _scroll.IsVisible = !guide;
        Pressed(_toGuide, guide);
        Pressed(_toSteps, !guide);
        // The drawing of the device needs the room, and naming a game does
        // nothing while a run is being walked through. The first build kept the
        // whole setup row and left the QuadStick as a sixty pixel sliver under
        // it, which is no better than not drawing it. Stop stays: a run in a
        // model call has to be stoppable from wherever you are looking.
        _ask.IsVisible = _go.IsVisible = _replay.IsVisible = !guide;
        _setup.IsVisible = !guide;
    }

    /// <summary>Which key of the view switch is down. Pressed reads as a fill
    /// and as the word "showing" to a screen reader, so it is never the colour
    /// on its own.</summary>
    static void Pressed(Button key, bool down)
    {
        key.Classes.Set("primary", down);
        AutomationProperties.SetItemStatus(key, down ? "showing" : "");
    }

    /// <summary>The whole profile, drawn on the device, before a single
    /// question. Walking somebody through their own mouthpiece first is what
    /// makes the questions after it answerable.</summary>
    void Map(AgentEvent e)
    {
        var rows = e.List("rows").Select(Placement).ToList();
        _guide = GuideFor(e);
        _guide.Walked = Walked;
        _guideHost.Content = _guide;
        _switch.IsVisible = true;
        Look(guide: true);
        // The guide's own first step says the same three counts. Saying them
        // again on the status line cost a row of the device's height to repeat
        // the sentence directly above it.
        Say("");
    }

    /// <summary>One map event as the guide it draws. Preview renders go through
    /// this too, so what a screenshot shows is what a run shows.</summary>
    internal static AgentGuide GuideFor(AgentEvent e) => new(
        e.Str("game"),
        e.List("rows").Select(Placement).ToList(),
        // What is not being bound travels as rows too, so the reason each one
        // carries is on screen with it rather than as a count.
        e.List("open").Select(Placement).ToList(),
        e.List("left").Select(Placement).ToList());

    /// <summary>The walkthrough ran out, so whatever was waiting on it happens
    /// now.</summary>
    void Walked()
    {
        if (_held.Count == 0)
        {
            // This step can sit in a model call for minutes. The seconds count
            // on the line below, and What it did has the call itself, so a wait
            // this long is never a screen that just stopped.
            _guide?.Waiting("Working out the next thing to ask you...",
                            "What it is doing right now is under What it did.");
            return;
        }
        Draw(_held.Dequeue());
    }

    /// <summary>One row of a map or a confirm as something with a place on the
    /// device.</summary>
    static Placed Placement(JsonElement row)
    {
        string Text(string name) => row.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var inputs = row.TryGetProperty("inputs", out var i) && i.ValueKind == JsonValueKind.Array
            ? i.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : new List<string>();
        // A settled row carries why it was settled; one still open carries the
        // question. Either way it is the sentence that goes under the name.
        var why = Text("why") is { Length: > 0 } said ? said : Text("question");
        return new Placed(Text("output"), Text("action"), inputs, Text("function"), why);
    }

    // ---- the two places a person decides something ------------------------

    void Question(AgentEvent e)
    {
        // Mid-walkthrough it waits its turn. The run is blocked on the answer
        // either way, so nothing is lost by finishing the tour first.
        if (_guide is { Walking: true }) { _held.Enqueue(e); return; }
        if (_guide is not null) { AskOnDevice(e); return; }
        AskInStream(e);
    }

    /// <summary>The question above the device, with each option lighting the
    /// part of the mouthpiece it would land on as it is reached.</summary>
    void AskOnDevice(AgentEvent e)
    {
        var output = e.Str("output");
        var options = e.List("options").Select(o =>
        {
            var row = o;
            string Text(string name) => row.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var inputs = row.TryGetProperty("inputs", out var i) && i.ValueKind == JsonValueKind.Array
                ? i.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>();
            return new Placed(output, Text("label"), inputs, Text("function"), "");
        }).ToList();
        // The offer to leave it alone is an option like any other, and the last
        // one, so it is never the thing a hurried hand lands on first.
        var leaveAt = options.Count;
        options.Add(new Placed(output, "Leave this one alone", Array.Empty<string>(), "", ""));

        Look(guide: true);
        _guide!.Ask(e.Str("question"),
            $"{AgentGuide.Speak(output)}. Nothing is written either way until you approve it.",
                    options, choice =>
        {
            var picked = options[choice];
            var what = choice == leaveAt ? "left alone"
                : picked.Inputs.Count > 0 ? $"{picked.Trigger}, {picked.Function}"
                : "left unbound";
            _guide.Chose(what, picked);
            // The transcript keeps the whole account, including the things that
            // were decided somewhere else. A person checking later must not have
            // to remember which view a decision was made in.
            Note($"Asked about {output}: {e.Str("question")}  You chose: {what}.");
            Send(e.Id, choice == leaveAt ? -1 : choice);
        });
    }

    void Send(string id, int choice) =>
        _bridge?.Reply(choice >= 0 ? new { id, choice } : new { id, choice = (int?)null });

    void AskInStream(AgentEvent e)
    {
        var options = e.List("options");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = e.Str("question"),
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = e.Str("output"),
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });

        var buttons = new StackPanel { Spacing = 6 };
        var all = new List<Button>();
        for (int n = 0; n < options.Count; n++)
        {
            var option = options[n];
            var label = option.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
            var inputs = option.TryGetProperty("inputs", out var i) && i.ValueKind == JsonValueKind.Array
                ? string.Join(" + ", i.EnumerateArray().Select(x => x.GetString())) : "";
            var function = option.TryGetProperty("function", out var f) ? f.GetString() ?? "" : "";
            var choice = n;
            var button = new Button
            {
                // Wide, tall and left aligned. These are read aloud and pressed
                // by people using a mouth stick or a head mouse.
                MinHeight = Size("ControlHeight"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = Size("BodySize"),
                                        TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = inputs.Length > 0 ? $"{inputs}, {function}" : "leaves it unbound",
                                        FontSize = Size("SmallSize"), Classes = { "muted" },
                                        TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            AutomationProperties.SetName(button, inputs.Length > 0
                ? $"{label}. Binds {inputs}, {function}."
                : $"{label}. Leaves this control unbound.");
            button.Click += (_, _) => Answered(e.Id, choice, all, panel,
                inputs.Length > 0 ? $"{inputs}, {function}" : "left unbound");
            all.Add(button);
            buttons.Children.Add(button);
        }

        var leave = new Button
        {
            Content = "Leave this one alone", MinHeight = Size("ControlHeight"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Classes = { "quiet" },
        };
        AutomationProperties.SetName(leave,
            $"Leave {e.Str("output")} alone. Nothing is written for it.");
        leave.Click += (_, _) => Answered(e.Id, -1, all, panel, "left alone");
        all.Add(leave);
        buttons.Children.Add(leave);

        panel.Children.Add(buttons);
        Add(Panel(panel, "SurfaceBrush", accent: true));
        // The first option takes focus, so the whole question can be answered
        // from the keyboard without hunting for it.
        Dispatcher.UIThread.Post(() => all.FirstOrDefault()?.Focus(), DispatcherPriority.Background);
    }

    void Answered(string id, int choice, List<Button> buttons, StackPanel panel, string what)
    {
        foreach (var b in buttons) b.IsEnabled = false;
        // What they chose stays on screen, in the words they chose it by. A
        // question that collapses to nothing leaves them unable to check later
        // what they agreed to.
        var chosen = new TextBlock
        {
            Text = $"You chose: {what}",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(chosen, AutomationLiveSetting.Polite);
        panel.Children.Add(chosen);
        Send(id, choice);
    }

    void Confirm(AgentEvent e)
    {
        if (_guide is { Walking: true }) { _held.Enqueue(e); return; }
        // The list to approve is a list, so it is read in the list. The device
        // has already done its job by this point: they have seen where every one
        // of these lands.
        if (_guide is not null) Look(guide: false);
        var rows = e.List("rows");
        var open = e.List("open");
        var untouched = e.List("untouched");

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = rows.Count == 1 ? "Write 1 binding?" : $"Write these {rows.Count} bindings?",
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Into {Path.GetFileName(e.Str("profile"))}. Nothing has been written yet.",
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var row in rows) list.Children.Add(BindingLine(row));
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 260, Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // What is NOT being written is part of what they are approving, so it
        // is on the same card and not somewhere they have to go looking.
        if (open.Count > 0 || untouched.Count > 0)
        {
            var left = open.Select(o => o.TryGetProperty("output", out var v) ? v.GetString() : null)
                .Concat(untouched.Select(u => u.GetString()))
                .Where(x => !string.IsNullOrEmpty(x)).ToList();
            panel.Children.Add(new TextBlock
            {
                Text = $"{left.Count} controls stay unbound and are left exactly as they are: "
                     + string.Join(", ", left),
                FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            });
        }

        var write = new Button { Content = "Write it", Classes = { "primary" }, MinWidth = 140 };
        AutomationProperties.SetName(write, $"Write these {rows.Count} bindings into the profile");
        var cancel = new Button { Content = "Do not write anything", MinWidth = 180 };
        AutomationProperties.SetName(cancel, "Write nothing and leave the profile exactly as it is");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { write, cancel },
        };
        panel.Children.Add(buttons);

        void Decide(bool yes)
        {
            write.IsEnabled = cancel.IsEnabled = false;
            Say(yes ? "Writing..." : "Nothing was written.");
            _bridge?.Reply(new { id = e.Id, write = yes });
        }
        write.Click += (_, _) => Decide(true);
        cancel.Click += (_, _) => Decide(false);

        Add(Panel(panel, "SurfaceBrush", accent: true));
        Dispatcher.UIThread.Post(() => write.Focus(), DispatcherPriority.Background);
    }

    void Written(AgentEvent e)
    {
        _written = e.Str("profile");
        var count = e.Num("written");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = count == 0 ? "Nothing was changed."
                              : $"{count} binding{(count == 1 ? "" : "s")} written to {Path.GetFileName(_written)}.",
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"The device reads it with {Count(e.Num("errors"), "error")} and "
                 + $"{Count(e.Num("warnings"), "warning")}.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        foreach (var issue in e.List("issues").Take(6))
            panel.Children.Add(new TextBlock
            {
                Text = $"{issue.GetProperty("severity").GetString()}: "
                     + $"{issue.GetProperty("cell").GetString()} "
                     + $"{issue.GetProperty("message").GetString()}",
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });

        if (count > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var open = new Button { Content = "Open it in the editor", Classes = { "primary" }, MinWidth = 180 };
            AutomationProperties.SetName(open,
                "Open the profile that was just written in the editor, where you can check it and install it");
            open.Click += (_, _) => Hand(OpenWritten);
            buttons.Children.Add(open);

            // Only offered when there is something to install onto. This does
            // not install anything by itself: it opens the profile and starts
            // the app's own install, which checks the file, asks which drive,
            // and asks again before replacing what is on the device.
            if (InstallWritten is { } install && Connected())
            {
                var now = new Button { Content = "Install it to your QuadStick", MinWidth = 220 };
                AutomationProperties.SetName(now,
                    "Open this profile and start installing it onto the QuadStick that is plugged in. "
                  + "It still asks you which drive and confirms before replacing anything.");
                now.Click += (_, _) => Hand(install);
                buttons.Children.Add(now);
            }
            panel.Children.Add(buttons);
            // It does not end here. The box at the top now points at the file
            // that was just written, so the next thing they say is a change to
            // it rather than a whole new game.
            panel.Children.Add(new TextBlock
            {
                Text = "Not quite right? Say what to change at the top, in your own words, "
                     + "and it changes that row and nothing else.",
                FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            });
            _ask.Text = "";
            _ask.Watermark = "make sprint a hard puff instead";
            AutomationProperties.SetName(_ask,
                $"A change to {Path.GetFileName(_written)}, in your own words");
            _go.Content = "Change it";
            AutomationProperties.SetName(_go, "Work out what to change, and show you before it changes anything");
        }

        Add(Panel(panel, "SurfaceBrush", accent: true));
        Say(count == 0 ? "Nothing was changed."
                       : $"Written to {_written}. Ask for a change above, or open it in the editor.");
    }

    static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    /// <summary>Hand the written profile to the app, and stay open if that
    /// fails so the reason is somewhere the person can read it.</summary>
    void Hand(Action<string> next)
    {
        try { next(_written!); Close(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Say($"Could not open it: {ex.Message}"); }
    }

    bool Connected()
    {
        // Looking for a drive touches the filesystem, and a machine with a
        // stalled mount should not take the window down with it.
        try { return DeviceConnected(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    void Failed(AgentEvent e)
    {
        // A run can fail after it has already replaced a file. Saying nothing
        // was written would send someone away believing their profile is
        // untouched when it is not.
        var wrote = e.List("wrote").Select(w => w.GetString())
                     .Where(w => !string.IsNullOrEmpty(w)).ToList();
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = wrote.Count == 0
                ? "Stopped. Nothing was written."
                : $"Stopped, but {string.Join(", ", wrote.Select(Path.GetFileName))} "
                + "had already been written by then. Check it before using it.",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = e.Str("message"), FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        Add(Panel(panel, "SurfaceSubtleBrush"));
        Say(wrote.Count == 0
            ? "Stopped. Nothing was written."
            : $"Stopped after writing {string.Join(", ", wrote.Select(Path.GetFileName))}.");
        if (wrote.Count > 0) _written = wrote[0];
    }

    // ---- the look ---------------------------------------------------------

    static Border Panel(Control content, string background, bool accent = false)
    {
        var panel = new Border
        {
            BorderThickness = new Thickness(accent ? 2 : 1),
            CornerRadius = new CornerRadius(Size("TrackRadius")),
            Padding = new Thickness(14, 12),
            Child = content,
        };
        Paint(panel, background, accent ? "AccentBrush" : "SurfaceBorderBrush");
        return panel;
    }

    // Colours are taken as dynamic resources, not looked up once. They live in
    // the theme dictionaries, so a one-off lookup finds nothing under the
    // default variant, and a card painted from a fixed brush would keep the old
    // theme's colours after somebody switches to dark.
    internal static void Paint(Border border, string background, string edge)
    {
        border[!BackgroundProperty] = new DynamicResourceExtension(background);
        border[!BorderBrushProperty] = new DynamicResourceExtension(edge);
    }

    internal static double Size(string token) => (double)Application.Current!.FindResource(token)!;

    /// <summary>One call the agent made, as a card: what it did, how it went,
    /// and, when opened, exactly what it was given and what came back.
    ///
    /// The state is a word as well as a glyph. Nothing in this window is ever
    /// signalled by colour alone.</summary>
    internal class ToolCard : Border
    {
        readonly TextBlock _title;
        readonly TextBlock _subtitle;
        readonly TextBlock _state;
        readonly Expander _detail;
        readonly StackPanel _head;

        internal string StateWord => _state.Text ?? "";

        /// <summary>Still working. A card counts its own seconds while this is
        /// true, because a step that takes half a minute with nothing moving is
        /// indistinguishable from a step that hung.</summary>
        internal bool Running { get; private set; }

        /// <summary>When this step started, so one timer can count for all of them.</summary>
        internal DateTime Began { get; } = DateTime.UtcNow;

        /// <summary>The run ended while this step was still going. It says that,
        /// rather than being left spinning forever on a card nobody will settle.</summary>
        internal void Abandon()
        {
            if (!Running) return;
            Running = false;
            Edge(false);
            _word = "✕ never finished";
            _state.Text = Label();
            Announce();
        }

        string _word = "";
        string _source = "";
        double _seconds;

        /// <summary>How long this card has been working, in words. Driven from
        /// one timer in the window rather than one per card, so a run with a
        /// hundred steps still has exactly one thing ticking.</summary>
        internal void Tick(double seconds)
        {
            if (!Running) return;
            _seconds = seconds;
            _state.Text = Label();
        }

        internal ToolCard(AgentEvent e)
        {
            // The step happening now is the one the eye should land on. This is
            // a second signal only: the word beside it already says "working",
            // so nothing here is carried by the colour on its own.
            Edge(e.State == "running");
            CornerRadius = new CornerRadius(Size("TrackRadius"));
            Padding = new Thickness(14, 12);

            _title = new TextBlock
            {
                Text = e.Title, FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
            };
            _subtitle = new TextBlock
            {
                Text = e.Subtitle, FontSize = Size("SmallSize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            };
            _word = Word(e.State);
            Running = e.State == "running";
            _source = Source(e.Str("origin"));
            _state = new TextBlock
            {
                Text = Label(), FontSize = Size("SmallSize"),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_state, Dock.Right);
            _state.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(_state);
            head.Children.Add(_title);

            // The card is its own expander. A separate row saying "what it was
            // given, and what came back" cost a line of height on every step of
            // a forty step run to say something the arrow already says, so the
            // card itself opens, and the raw call stays one click away for
            // anyone who wants to check it.
            _head = new StackPanel { Spacing = 2 };
            _head.Children.Add(head);
            if (e.Subtitle.Length > 0) _head.Children.Add(_subtitle);
            _detail = new Expander
            {
                Header = _head,
                FontSize = Size("SmallSize"),
                Content = Body(e),
            };
            AutomationProperties.SetName(_detail,
                $"{e.Title}. Opens to show exactly what this step was given and what it returned");

            Child = _detail;
            Announce();
        }

        void Edge(bool working)
        {
            Paint(this, working ? "SurfaceBrush" : "SurfaceSubtleBrush",
                  working ? "AccentBrush" : "SurfaceBorderBrush");
            BorderThickness = new Thickness(working ? 2 : 1);
        }

        internal void Settle(AgentEvent e)
        {
            Running = false;
            Edge(false);
            _word = Word(e.State);
            // What the run reports it took beats what the window happened to
            // observe. The run is the thing that did the work.
            if (e.Get("ms") is { ValueKind: JsonValueKind.Number } ms) _seconds = ms.GetDouble() / 1000;
            if (Source(e.Str("origin")) is { Length: > 0 } where) _source = where;
            _state.Text = Label();
            if (e.Str("summary") is { Length: > 0 } summary)
            {
                _subtitle.Text = summary;
                if (!_head.Children.Contains(_subtitle)) _head.Children.Add(_subtitle);
            }
            _detail.Content = Body(e);
            Announce();
        }

        void Announce() =>
            AutomationProperties.SetName(this, $"{_title.Text}. {_state.Text}. {_subtitle.Text}");

        // The glyph is a second signal, never the only one: the word beside it
        // carries the same fact for anyone who cannot see the mark.
        static string Word(string state) => state switch
        {
            "running" => "◌ working",
            "ok" => "✓ done",
            "warn" => "! worth a look",
            "failed" => "✕ refused",
            _ => state,
        };

        /// <summary>Where this step's answer came from, said plainly.
        ///
        /// A run that finishes in a second because every answer was already on
        /// disk looks exactly like a run that invented them. This is the line
        /// that tells them apart, and it is on every card rather than in a
        /// footnote, because it is the first thing anyone doubts.</summary>
        internal static string Source(string origin) => origin switch
        {
            "live" => "asked the model",
            "cache" => "from the recording",
            "local" => "on this machine, no model",
            _ => "",
        };

        string Label()
        {
            var took = _seconds > 0 ? $"  {_seconds:0.0}s" : "";
            var where = _source.Length > 0 && !Running ? $"  ·  {_source}" : "";
            return _word + took + where;
        }

        static Control Body(AgentEvent e) => new ScrollViewer
        {
            MaxHeight = 220,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock
            {
                Text = Pretty(e.Raw),
                FontFamily = FontFamily.Parse("Menlo, Consolas, monospace"),
                FontSize = Size("SmallSize") - 1,
                TextWrapping = TextWrapping.NoWrap,
            },
        };

        static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        static string Pretty(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return JsonSerializer.Serialize(doc.RootElement, Indented);
            }
            catch (JsonException) { return raw; }
        }
    }
}
