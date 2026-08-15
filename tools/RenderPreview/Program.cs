using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;

// Renders MainWindow to PNGs for docs, and the appearance gallery beside them,
// so a change to Style.cs or Palette.cs can be looked at in both themes without
// launching anything. Usage: RenderPreview [out-dir] [corpus-dir] [map-event]
//
// map-event is one line of agent output, the map event of a real run. Given
// one, the walkthrough is drawn from it at every step, because a guide that
// looks fine with three rows can be unusable with fifty.

var outDir = args.Length > 0 ? args[0] : "/tmp/qscm-renders";
var corpus = args.Length > 1 ? args[1] : "tests/QuadStick.Format.Tests/corpus";
var mapEvent = args.Length > 2 && File.Exists(args[2]) ? File.ReadAllText(args[2]) : null;
Directory.CreateDirectory(outDir);

// Fake a profile library so the home screen has cards to show.
var lib = Directory.CreateTempSubdirectory("qscm-lib-").FullName;
File.Copy(Path.Combine(corpus, "gta-mode1.csv"), Path.Combine(lib, "gta.csv"));
File.Copy(Path.Combine(corpus, "default.csv"), Path.Combine(lib, "rocket-league.csv"));
MainWindow.LibraryDir = lib;

Environment.SetEnvironmentVariable("QSCM_TELEMETRY", "0");   // screenshots never send

// One question and one approval, shaped as agent/run.py emits them, so the two
// screens a person actually decides something on are rendered too.
const string Ask = """
    {"event":"question","id":"q1","output":"left_joy_up",
     "question":"Walking forward: tilt the joystick, or a puff that keeps you going?",
     "options":[{"inputs":["up"],"function":"normal","label":"Tilt the mouthpiece forward"},
                {"inputs":["mp_center_puff"],"function":"toggle","label":"Puff on the centre hole, press once"}]}
    """;
// The list they decide over, with the two ways of declining on it: a tick per
// row, and a box to say what is wrong in their own words.
const string Settled = """
    {"event":"confirm","id":"c1","profile":"/tmp/elden-ring.csv","canSay":true,
     "rows":[{"output":"left_joy_up","action":"Move character forward","inputs":["up"],
              "function":"normal","why":"you answered the question about this control"},
             {"output":"kb_left_shift","action":"Sprint","inputs":["mp_triple_puff"],
              "function":"delay_on 500 16000","why":"24 of 46 of the published profiles do this (52%); nearest example Apex Legends, mode 'Gameplay', row 19"},
             {"output":"kb_space","action":"Jump","inputs":["lip"],"function":"normal",
              "why":"every platformer in the corpus does this"},
             {"output":"kb_c","action":"Crouch","inputs":["mp_left_sip"],"function":"toggle",
              "why":"a guess across games, not something his own profiles settle"}],
     "open":[{"output":"kb_v","question":"Melee: hold it, or press once?"}],
     "untouched":["kb_f"]}
    """;

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

foreach (var (suffix, variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
{
    Application.Current!.RequestedThemeVariant = variant;

    // The specimen sheet: what you compare against after a token changes.
    // Tall on purpose, or the render stops at the fold and hides the colours.
    CaptureWindow($"{suffix}-0-gallery", new GalleryWindow { Height = 2300 });

    Capture($"{suffix}-1-home", _ => { });

    CaptureOwned($"{suffix}-1b-settings", owner => new SettingsWindow(owner));
    CaptureOwned($"{suffix}-1c-game-setup", owner => new AgentWindow(owner));

    // The whole window as somebody meets it: the walkthrough, then a question
    // asked over the same picture, with the chrome that goes round both.
    if (mapEvent is not null)
    {
        CaptureRun($"{suffix}-1e-run-walk", new[] { mapEvent });
        CaptureRun($"{suffix}-1e2-run-crowded", new[] { mapEvent }, next: 1);
        // The last step, where everything not being bound is said. It carries
        // the most words of any step, so it is the one that has to be looked at.
        CaptureRun($"{suffix}-1e3-run-last", new[] { mapEvent }, next: 9);
        CaptureRun($"{suffix}-1f-run-asking", new[] { mapEvent, Ask }, skipWalk: true);
        CaptureRun($"{suffix}-1g-run-confirm", new[] { mapEvent, Settled }, skipWalk: true);
        // The same card with a row taken off it. A state nobody has looked at
        // is a state nobody has checked, and this is the one where the count,
        // the button and the list all have to agree.
        CaptureRun($"{suffix}-1h-run-declined", new[] { mapEvent, Settled },
                   skipWalk: true, untick: 2);
    }

    if (mapEvent is not null)
        for (int step = 0; step < 12; step++)
        {
            var at = step;
            var guide = AgentWindow.GuideFor(Event(mapEvent));
            var win = new Window
            {
                Width = 1024, Height = 768, Content = guide,
                SystemDecorations = SystemDecorations.None,
            };
            win.Show();
            Dispatcher.UIThread.RunJobs();
            if (!guide.StepForPreview(at)) { win.Close(); break; }
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
            CaptureWindow($"{suffix}-1d-guide-{at:00}", win, shown: true);
        }

    Capture($"{suffix}-2-gta-loaded", w =>
        w.LoadProfile(ProfileFile.Load(File.ReadAllText(Path.Combine(corpus, "gta-mode1.csv")))));

    Capture($"{suffix}-3-errors", w =>
    {
        var f = ProfileFile.Load(File.ReadAllText(Path.Combine(corpus, "gta-mode1.csv")));
        f.SetCell(4, 1, "blink");        // unknown function
        f.SetCell(5, 2, "left_sip");     // unknown input
        w.LoadProfile(f);
        w.SetDeviceViewForPreview(false); // list view shows the bad cells
        w.ShowProblemsForPreview();       // and the plain English fix
    });

    Capture($"{suffix}-4-new-from-template", w =>
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv")));

    Capture($"{suffix}-5-device-view", w =>
    {
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SelectZoneForPreview("mp_left");
    });

    // The two tight cases: every toolbar control has to stay on screen in both.
    // Scaled is the harsher one, since scale divides the width the layout gets.
    Capture($"{suffix}-8-narrow", w =>
    {
        w.Width = 760; w.Height = 560;
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SetDeviceViewForPreview(false);
    });

    Capture($"{suffix}-9-scaled-200", w =>
    {
        w.ApplyInterfaceScale(200);
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SetDeviceViewForPreview(false);
    });

    Capture($"{suffix}-7-unused-inputs", w =>
    {
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SetDeviceViewForPreview(false);
        w.ShowUnusedForPreview();
    });

    Capture($"{suffix}-10-custom-names", w =>
    {
        w.LoadProfile(ProfileFile.Load(File.ReadAllText(Path.Combine(corpus, "gta-mode1.csv"))));
        w.SelectCustomNamesForPreview();
        w.AddRowForPreview();
    });

    Capture($"{suffix}-6-singleton", w =>
    {
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SetModelForPreview(2); // Singleton
        w.SelectZoneForPreview("mp_center");
    });
}

Console.WriteLine($"Renders written to {outDir}");
return;

/// <summary>The agent window driven by a scripted run, the way a person sees
/// it: type a game, press the button, watch the events land.</summary>
void CaptureRun(string name, IReadOnlyList<string> events, bool skipWalk = false, int next = 0,
                int untick = -1)
{
    var owner = new MainWindow();
    owner.Show();
    Dispatcher.UIThread.RunJobs();

    var run = new Scripted();
    var win = new AgentWindow(owner, root: "/nowhere") { StartWith = _ => run };
    win.Show(owner);
    Dispatcher.UIThread.RunJobs();
    run.Watching = win;

    win.GetVisualDescendants().OfType<TextBox>().First().Text = "Elden Ring";
    Dispatcher.UIThread.RunJobs();
    Click(win, "Set it up");

    foreach (var line in events)
    {
        run.Say(line.ReplaceLineEndings(" "));
        if (skipWalk) { Click(win, "Skip"); skipWalk = false; }
    }
    for (int n = 0; n < next; n++) Click(win, "Next");
    if (untick >= 0)
    {
        // The row ticks carry a binding; the replay checkbox up top carries a
        // string, so this picks the ones on the list being approved.
        var tick = win.GetVisualDescendants().OfType<CheckBox>()
                      .Where(c => c.Content is Control).Skip(untick).FirstOrDefault();
        if (tick is not null) tick.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
    }
    win.UpdateLayout();
    // The window scrolls the transcript on a posted job, which in here runs
    // before the new card has been laid out, so the shot is taken at a scroll
    // position the app never actually sits at. Settled here instead.
    foreach (var scroll in win.GetVisualDescendants().OfType<ScrollViewer>())
        if (AutomationProperties.GetName(scroll) is "What the agent has done so far")
            scroll.ScrollToEnd();
    Dispatcher.UIThread.RunJobs();
    win.UpdateLayout();
    CaptureWindow(name, win, shown: true);
    owner.Close();
    Dispatcher.UIThread.RunJobs();
}

void Click(Window win, string label)
{
    var button = win.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
        (b.Content as string ?? string.Join(" ", (b.Content as Control)?
            .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text) ?? []))
        .Contains(label, StringComparison.OrdinalIgnoreCase));
    button?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    win.UpdateLayout();
}

AgentEvent Event(string json) => new()
{
    Kind = "map", Raw = json,
    Body = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
};

void Capture(string name, Action<MainWindow> setup)
{
    var win = new MainWindow();
    win.Show();
    Dispatcher.UIThread.RunJobs();
    setup(win);
    CaptureWindow(name, win, shown: true);
}

void CaptureWindow(string name, Window win, bool shown = false)
{
    if (!shown) { win.Show(); }
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    using var frame = win.CaptureRenderedFrame()
        ?? throw new InvalidOperationException("No frame captured");
    frame.Save(Path.Combine(outDir, name + ".png"));
    win.Close();
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine($"  {name}.png");
}

void CaptureOwned(string name, Func<MainWindow, Window> create)
{
    var owner = new MainWindow();
    owner.Show();
    Dispatcher.UIThread.RunJobs();
    var win = create(owner);
    win.Show(owner);
    Dispatcher.UIThread.RunJobs();
    CaptureWindow(name, win, shown: true);
    owner.Close();
    Dispatcher.UIThread.RunJobs();
}

/// <summary>A run whose events this hands over one at a time. No model, no
/// network, no python: the same seam the window's tests drive.</summary>
sealed class Scripted : IAgentRun
{
    public event Action<AgentEvent>? Event;
    // Nothing here ever fails or ends: these exist because the interface
    // has them, and a screenshot run has no failure to report.
#pragma warning disable CS0067
    public event Action<string>? Trouble;
    public event Action<int>? Ended;
#pragma warning restore CS0067

    public Window? Watching { get; set; }

    public void Start() { }

    public void Say(string json)
    {
        var body = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        string Text(string name) => body.TryGetProperty(name, out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        Event?.Invoke(new AgentEvent
        {
            Kind = Text("event"), Id = Text("id"), Title = Text("title"),
            Subtitle = Text("subtitle"), Text = Text("text"), State = Text("state"),
            Raw = json, Body = body,
        });
        Dispatcher.UIThread.RunJobs();
        Watching?.UpdateLayout();
    }

    public void Reply(object answer) { }
    public void Dispose() { }
}
