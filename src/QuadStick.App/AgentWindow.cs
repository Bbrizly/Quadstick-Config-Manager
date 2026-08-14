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
// Every step the agent takes is a card in this window, in the order it happened,
// with what it was given and what came back. That is not decoration. A profile
// arriving fully formed with no account of where each row came from is exactly
// the thing a person cannot check, and this is a file they steer their computer
// with. So the transcript IS the feature: the run explains itself as it goes,
// and the approval at the end is over a list they have already watched being
// built.
//
// Nothing here writes a cell. The agent process does that, behind the same
// refusals the terminal path uses, so there is one write path and not two.
public class AgentWindow : Window
{
    readonly MainWindow _owner;
    readonly string _root;

    readonly TextBox _ask;
    readonly Button _go;
    readonly Button _close;
    readonly StackPanel _stream;
    readonly ScrollViewer _scroll;
    readonly TextBlock _status;
    readonly CheckBox _replay;
    readonly Dictionary<string, ToolCard> _cards = new();

    IAgentRun? _bridge;
    string? _written;
    bool _running;

    /// <summary>Test seam: what a finished run does with the profile it wrote.
    /// The app opens it in the editor; a test just records the path.</summary>
    internal Action<string> OpenWritten { get; set; }

    /// <summary>Test seam: how the agent process gets started. Tests hand this
    /// a bridge over a scripted stream so no model and no network are involved.</summary>
    internal Func<IReadOnlyList<string>, IAgentRun>? StartWith { get; set; }

    public AgentWindow(MainWindow owner) : this(owner, AgentBridge.FindAgentRoot()) { }

    internal AgentWindow(MainWindow owner, string? root)
    {
        _owner = owner;
        _root = root ?? "";
        OpenWritten = path => _owner.OpenPath(path);
        Title = "Set up a game";
        Width = Math.Min(760 * owner.UiScale, 1100);
        Height = Math.Min(700 * owner.UiScale, 900);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = "Set up a game",
            FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
        };
        var explain = new TextBlock
        {
            Text = "Name any game or app. It reads how that game is controlled, then works out "
                 + "what each control should be on your QuadStick from the profiles you have "
                 + "already built. It asks you about anything your own profiles do not settle, "
                 + "and it writes nothing until you say so.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _ask = new TextBox
        {
            Watermark = "A game, or a change to make: Hollow Knight Silksong",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_ask, "The game to set up");
        _ask.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Begin(); } };

        _go = new Button { Content = "Set it up", Classes = { "primary" }, MinWidth = 130 };
        AutomationProperties.SetName(_go, "Start setting up the game you named");
        _go.Click += (_, _) => Begin();

        _replay = new CheckBox { Content = "From the recording", IsChecked = false };
        AutomationProperties.SetName(_replay,
            "Run from the recorded answers instead of asking the model again. Needs no internet.");

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
            Text = _root.Length > 0 ? "" : "The agent files are not next to this app, so nothing can run.",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _close = new Button { Content = "Close", MinWidth = 130, IsCancel = true };
        AutomationProperties.SetName(_close, "Close this window");
        _close.Click += (_, _) => Close();

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(_go);
        top.Children.Add(_replay);

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        foreach (var (control, dock, margin) in new (Control, Dock, Thickness)[]
        {
            (heading, Dock.Top, new Thickness(0, 0, 0, 8)),
            (explain, Dock.Top, new Thickness(0, 0, 0, 12)),
            (_ask, Dock.Top, new Thickness(0, 0, 0, 10)),
            (top, Dock.Top, new Thickness(0, 0, 0, 14)),
            (_status, Dock.Bottom, new Thickness(0, 12, 0, 0)),
            (_close, Dock.Bottom, new Thickness(0, 12, 0, 0)),
        })
        {
            control.Margin = margin;
            DockPanel.SetDock(control, dock);
            panel.Children.Add(control);
        }
        _close.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_scroll);

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
    internal IReadOnlyList<string> Arguments(string said, string? openProfile, bool replay)
    {
        var words = said.Trim();
        var list = new List<string>();
        if (openProfile is not null && LooksLikeAChange(words))
        {
            list.Add("--edit"); list.Add(openProfile);
            list.Add("--request"); list.Add(words);
        }
        else
        {
            list.Add("--game"); list.Add(words);
        }
        if (replay) list.Add("--replay");
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
        if (said.Length == 0) { Say("Type a game first, then press Set it up."); _ask.Focus(); return; }
        if (_root.Length == 0 && StartWith is null)
        {
            Say("The agent files are not next to this app, so nothing can run.");
            return;
        }

        _stream.Children.Clear();
        _cards.Clear();
        _written = null;
        _running = true;
        _go.IsEnabled = false;
        _ask.IsEnabled = false;
        Say($"Setting up {said}...");

        var arguments = Arguments(said, _owner.CurrentProfilePath, _replay.IsChecked == true);
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
            _ask.IsEnabled = true;
            Say($"The agent could not be started: {ex.Message}");
        }
    }

    void Finished(int code)
    {
        _running = false;
        _go.IsEnabled = true;
        _ask.IsEnabled = true;
        // A run that ends without having said why is the one thing this window
        // must never do quietly, so the exit code is turned into a sentence.
        if (code != 0 && _written is null && _status.Text?.StartsWith("Setting up") == true)
            Say("The run stopped before it finished, and nothing was written.");
        else if (_written is null && _status.Text?.StartsWith("Setting up") == true)
            Say("The run finished without writing anything.");
    }

    void Say(string message)
    {
        _status.Text = message;
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
            case "stage": Stage(e.Title); break;
            case "tool": Add(new ToolCard(e), e.Id); break;
            case "tool_done": Done(e); break;
            case "note": Note(e.Text); break;
            case "rows": Rows(e); break;
            case "question": Question(e); break;
            case "confirm": Confirm(e); break;
            case "done": Written(e); break;
            case "failed": Failed(e); break;
        }
    }

    void Add(Control card, string? id = null)
    {
        _stream.Children.Add(card);
        if (id is { Length: > 0 } && card is ToolCard tool) _cards[id] = tool;
    }

    void Done(AgentEvent e)
    {
        if (_cards.TryGetValue(e.Id, out var card)) card.Settle(e);
        // A result for a card nobody started is still something the agent said,
        // so it becomes its own card rather than being dropped.
        else Add(new ToolCard(e));
    }

    void Stage(string title)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 2) };
        var rule = new Border { Height = 1 };
        rule[!BackgroundProperty] = new DynamicResourceExtension("SurfaceBorderBrush");
        panel.Children.Add(rule);
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), Classes = { "section" }, TextWrapping = TextWrapping.Wrap,
        });
        Add(panel);
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

        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{output}   {inputs}, {function}",
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
            $"{output} bound to {inputs}, {function}. {(was.Length > 0 ? $"Was {was}. " : "")}{why}");
        return stack;
    }

    // ---- the two places a person decides something ------------------------

    void Question(AgentEvent e)
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
        _bridge?.Reply(choice >= 0 ? new { id, choice } : new { id, choice = (int?)null });
    }

    void Confirm(AgentEvent e)
    {
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
            var open = new Button { Content = "Open it in the editor", Classes = { "primary" }, MinWidth = 180 };
            AutomationProperties.SetName(open,
                "Open the profile that was just written in the editor, where you can check it and install it");
            open.Click += (_, _) =>
            {
                try { OpenWritten(_written!); Close(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Say($"Could not open it: {ex.Message}"); }
            };
            panel.Children.Add(open);
        }

        Add(Panel(panel, "SurfaceBrush", accent: true));
        Say(count == 0 ? "Nothing was changed."
                       : $"Written to {_written}. Open it in the editor to check it and install it.");
    }

    static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    void Failed(AgentEvent e)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Stopped. Nothing was written.",
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = e.Str("message"), FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        });
        Add(Panel(panel, "SurfaceSubtleBrush"));
        Say("Stopped. Nothing was written.");
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

    static double Size(string token) => (double)Application.Current!.FindResource(token)!;

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

        internal string StateWord => _state.Text ?? "";

        internal ToolCard(AgentEvent e)
        {
            Paint(this, "SurfaceSubtleBrush", "SurfaceBorderBrush");
            BorderThickness = new Thickness(1);
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
            _state = new TextBlock
            {
                Text = Word(e.State), FontSize = Size("SmallSize"),
                VerticalAlignment = VerticalAlignment.Top,
            };

            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_state, Dock.Right);
            _state.Margin = new Thickness(10, 0, 0, 0);
            head.Children.Add(_state);
            head.Children.Add(_title);

            _detail = new Expander
            {
                Header = "What it was given, and what came back",
                FontSize = Size("SmallSize"),
                Margin = new Thickness(0, 6, 0, 0),
                Content = Body(e),
            };
            AutomationProperties.SetName(_detail,
                "Show exactly what this step was given and what it returned");

            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(head);
            if (e.Subtitle.Length > 0) stack.Children.Add(_subtitle);
            stack.Children.Add(_detail);
            Child = stack;
            Announce();
        }

        internal void Settle(AgentEvent e)
        {
            _state.Text = Word(e.State);
            if (e.Str("summary") is { Length: > 0 } summary)
            {
                _subtitle.Text = summary;
                if (!((StackPanel)Child!).Children.Contains(_subtitle))
                    ((StackPanel)Child!).Children.Insert(1, _subtitle);
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
