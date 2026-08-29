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

// Screenshots are the app as it ships, not as this machine has it. Reading the
// real settings file put whatever model, theme and card style this developer
// last chose into the docs: every device-view shot went out saying "Not on
// model" because a Singleton was picked here months ago.
var langAt = Array.IndexOf(args, "--lang");
var lang = langAt >= 0 && langAt + 1 < args.Length ? args[langAt + 1] : Localization.FollowSystem;

var cfgDir = Directory.CreateTempSubdirectory("qscm-cfg-").FullName;
Settings.PathOverride = Path.Combine(cfgDir, "settings.json");
Settings.Save(new AppSettings { TutorialSeen = true, RememberWindow = false, Language = lang });

// --pseudo draws the whole app in the pseudo language: every label that is
// still plain English is one the move to Strings.resx missed, and every label
// that runs out of its control is a layout that assumed English.
// Relocalize as well as Apply: the preference catalog is translated as data
// rather than as resources, and without this the pseudo pass drew all sixty
// one setting labels in plain English, which is the half of this screen the
// check exists to look at.
if (args.Contains("--pseudo"))
{
    Localization.Apply("qps-ploc");
    Localization.Relocalize();
}

// A prefs.csv shaped like the one a QuadStick ships with: a handful of the
// settings written down, the rest left to the device's own defaults.
// One button held, so the shot shows the line that answers "did my sip
// register?" rather than the resting one.
int[] PressedForShot() => new[] { 2 };

const string SamplePrefs =
    "Preferences\nprefs.csv\nPreference,Value,Units,Description\n"
    + "joystick_deflection_minimum,8,,\n"
    + "joystick_deflection_maximum,25,,\n"
    + "joystick_D_Pad_inner,25,,\n"
    + "joystick_D_Pad_outer,80,,\n"
    + "sip_puff_threshold_soft,8,,\n"
    + "sip_puff_threshold,40,,\n"
    + "sip_puff_maximum,70,,\n"
    + "sip_puff_delay_soft,1300,,\n"
    + "lip_position_minimum,8,,\n"
    + "lip_position_maximum,35,,\n"
    + "mouse_speed,100,,\n"
    + "volume,40,,\n"
    + "brightness,75,,\n";


// --lang <tag> draws it in a shipped language. The store shots need one set
// per language, and Arabic is the one that has to be looked at rather than
// asserted: the window mirrors and the device photo must not follow it.
Localization.Apply(lang);

// A library that looks like somebody's. Real community workbooks, under the
// names their games have, so every card carries real modes and real counts:
// two files called "gta" and "rocket-league" is what a fixture looks like.
var lib = Directory.CreateTempSubdirectory("qscm-lib-").FullName;
var shelf = new (string Book, string Name, int DaysAgo)[]
{
    ("Forza-Horizon-4", "Forza Horizon 4", 1),
    ("Apex", "Apex Legends", 3),
    ("Starfield", "Starfield", 8),
    ("Valheim", "Valheim", 14),
    ("Sea-of-Thieves", "Sea of Thieves", 26),
    ("Zelda-Breath-of-the-Wild", "Zelda Breath of the Wild", 40),
};
// Fixed, so two runs of this tool produce the same picture.
var today = new DateTime(2026, 8, 25, 11, 0, 0, DateTimeKind.Local);
var books = args.Length > 3 ? args[3] : "agent/corpus/silas";
foreach (var (book, name, daysAgo) in shelf)
{
    var src = Path.Combine(books, book + ".xlsx");
    if (!File.Exists(src)) continue;
    var to = Path.Combine(lib, name + ".csv");
    using (var stream = File.OpenRead(src)) File.WriteAllText(to, Xlsx.ToCsv(stream));
    File.SetLastWriteTime(to, today.AddDays(-daysAgo));
}
// Nothing on the shelf means no workbooks on this machine. Still render, so a
// checkout without the corpus can look at the chrome.
if (Directory.GetFiles(lib, "*.csv").Length == 0)
    File.Copy(Path.Combine(corpus, "gta-mode1.csv"), Path.Combine(lib, "GTA.csv"));
MainWindow.LibraryDir = lib;
// Six real modes and one honest warning, which is what a profile somebody
// actually plays looks like. Nothing in the community corpus validates
// perfectly clean, and a screenshot should not pretend otherwise.
var hero = Directory.GetFiles(lib, "Forza Horizon 4.csv").FirstOrDefault()
        ?? Directory.GetFiles(lib, "*.csv").First();

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

// The set for Drew: one shot per thing he asked for, named after his own
// numbering, so a reply to his email can point at a picture instead of
// describing a screen. Light theme only, since these go in an email.
if (args.Contains("--drew"))
{
    Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    Settings.Save(new AppSettings { TutorialSeen = true, RememberWindow = false, DeviceCards = false, Language = lang });

    // The six settings Drew named, in one Preferences sheet, so the shot shows
    // the controls rather than an empty tab.
    string Prefs(string file, int emulation) => string.Join("\n", new[]
    {
        "Profile Name,,Gameplay", file, "Outputs,Function,usb",
        "kb_space,normal,lip", "", "Preferences", "",
        "Preference,Value,Units,Description",
        $"enable_DS3_emulation,{emulation}",
        "sip_puff_threshold,60",
        "sip_puff_delay_soft,1300",
        "titan_two,1",
        "enable_usb_a_host,1",
        "joystick_deflection_maximum,40",
    });

    int PrefSheetOf(ProfileFile f) =>
        f.Document.Sheets.ToList().FindIndex(x => x.Type == SheetType.Preferences);

    // 1. The device's own settings, on a screen of their own, in plain words
    // and with the names QMP uses for them, so somebody who knows QMP finds a
    // setting by the words they already have. This was a spreadsheet tab when
    // Drew first saw it; it is one of the app's three destinations now.
    Capture("1-device-settings", w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Sip and puff"));

    // The same screen for the group Drew asked about by name, with the group
    // list, the picture and the sliders in one shot.
    Capture("1b-device-joystick", w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Joystick"));

    // 2. Emulation mode in default.csv, the file the device boots into, set to
    // one of the four that take the drive away. Refused, not warned about.
    Capture("2-emulation-blocked", w =>
    {
        var f = ProfileFile.Load(Prefs("default.csv", 5));
        w.LoadProfile(f);
        w.SetDeviceViewForPreview(false);
        w.SelectSheetForPreview(PrefSheetOf(f));
        w.ShowProblemsForPreview();
    });

    // 3. The back panel: the photo with each socket named and the number a
    // single switch lands on written next to it.
    Capture("3-back-panel", w =>
    {
        w.Height = 1250;   // tall enough for the eight sockets under the picture
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SelectZoneForPreview("jacks");
    });

    // 3b. A joystick in the rear USB-A port, with its four directions, which
    // had no way into the picker before.
    Capture("3b-rear-joystick", w =>
    {
        w.Height = 1100;
        w.LoadProfile(ProfileFile.NewFromTemplate("mygame.csv"));
        w.SelectZoneForPreview("other");
    });

    // Three rows whose functions take numbers, so the hint under the box has
    // something to explain. Real functions, real shapes: a tap window, a delay
    // that latches, and a threshold in percent.
    string Funcs(string first) => string.Join("\n", new[]
    {
        "Profile Name,,Gameplay", "mygame.csv", "Outputs,Function,usb",
        $"kb_space,{first},lip",
        "kb_left_shift,delay_on 500 1,mp_triple_puff",
        "mouse_left_button,greater_than 60,mp_left_sip",
    });

    // 4. What a function's numbers mean: the range, the unit, and what the
    // device does when the cell leaves the number out.
    Capture("4-function-numbers", w =>
    {
        w.Height = 1000;
        w.LoadProfile(ProfileFile.Load(Funcs("tap 500 100")));
        w.SelectZoneForPreview("lip");
    });

    // 4b. A number past what the device can hold, said out loud rather than
    // quietly corrected. 150 percent is a level no input reaches, so the row
    // never fires; QCM says so and still saves what was typed.
    Capture("4b-out-of-range", w =>
    {
        w.LoadProfile(ProfileFile.Load(Funcs("greater_than 150")));
        w.SetDeviceViewForPreview(false);
        w.ShowProblemsForPreview();
    });

    // 5. USB or Bluetooth, per mode, in the file itself. Three modes with
    // three different answers, since one mode with one dropdown does not show
    // that the setting is per mode.
    const string ThreeModes = """
        Profile Name,,Gameplay,,,,,,,,Comments
        mygame.csv
        Outputs,Function,usb
        kb_space,normal,lip

        Profile Name,,Couch
        mygame.csv
        Outputs,Function,bluetooth
        kb_space,normal,lip

        Profile Name,,Desk and phone
        mygame.csv
        Outputs,Function,both
        kb_space,normal,lip

        Preferences

        Preference,Value,Units,Description
        sip_puff_threshold,60
        """;
    CaptureOwned("5-bluetooth-per-mode", owner =>
    {
        owner.LoadProfile(ProfileFile.Load(ThreeModes));
        return new ModesWindow(owner);
    });

    Console.WriteLine($"Drew's set written to {outDir}");
    return;
}

foreach (var (suffix, variant) in new[] { ("light", ThemeVariant.Light), ("dark", ThemeVariant.Dark) })
{
    Application.Current!.RequestedThemeVariant = variant;
    // Fresh settings per theme: opening a profile below files it under recents,
    // and the second pass would otherwise show a Home the first one did not.
    Settings.Save(new AppSettings { TutorialSeen = true, RememberWindow = false, Language = lang });

// --pseudo draws the whole app in the pseudo language: every label that is
// still plain English is one the move to Strings.resx missed, and every label
// that runs out of its control is a layout that assumed English.
// Relocalize as well as Apply: the preference catalog is translated as data
// rather than as resources, and without this the pseudo pass drew all sixty
// one setting labels in plain English, which is the half of this screen the
// check exists to look at.
if (args.Contains("--pseudo"))
{
    Localization.Apply("qps-ploc");
    Localization.Relocalize();
}

    // The specimen sheet: what you compare against after a token changes.
    // Tall on purpose, or the render stops at the fold and hides the colours.
    CaptureWindow($"{suffix}-0-gallery", new GalleryWindow { Height = 2300 });

    Capture($"{suffix}-1-home", w => w.Height = 900);

    Capture($"{suffix}-1b-settings", w => w.ShowSettingsPage());
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

    // The shot the README leads with: a real profile, open on the picture of
    // the hardware, with a part picked so the panel beside it is doing its job.
    // "Nothing selected" was half the window on the old one.
    // Tuning the device itself. No machine running this has a QuadStick on it,
    // so the page is drawn from a prefs.csv held in memory and a made-up stick
    // reading: the point of the shot is the picture, the group list and how
    // little of the page is text.
    Capture($"{suffix}-1i-device", w =>
    {
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Joystick");
        w.ShowLiveInputForPreview(new LiveState(0.42, -0.30, PressedForShot(), "QuadStick"));
    });

    // The group somebody opens to tune a mouthpiece, and the same page with
    // nothing plugged in, which is how it looks on most machines.
    Capture($"{suffix}-1i2-device-sip", w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Sip and puff"));

    Capture($"{suffix}-1i3-device-unplugged", w =>
        w.ShowDeviceSettingsForPreview(root: null, category: "Sound and lights"));

    // The group with the on/off boxes and the dropdowns in it, and the same
    // page at the narrowest window the app opens to. Sliders are the easy
    // shape; these two are where a settings screen falls over.
    Capture($"{suffix}-1i4-device-usb", w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "USB and compatibility"));

    Capture($"{suffix}-1i5-device-bluetooth", w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Bluetooth"));

    CaptureSized($"{suffix}-1i6-device-narrow", 860, 620, w =>
        w.ShowDeviceSettingsForPreview(SamplePrefs, category: "Sip and puff"));

    Capture($"{suffix}-1j-community", w => w.ShowCommunityPage());

    Capture($"{suffix}-2-editor", w =>
    {
        w.OpenPathForPreview(hero);
        w.SelectZoneForPreview("joystick");
    });

    // Rows: what the file really is, one line per mapping. A different profile
    // from the hero on purpose, one whose first screen is bound rather than a
    // column of "pick an input", since that is what the view is for.
    Capture($"{suffix}-2b-rows", w =>
    {
        w.OpenPathForPreview(Path.Combine(lib, "Apex Legends.csv") is var dense
            && File.Exists(dense) ? dense : hero);
        w.SetDeviceViewForPreview(false);
    });

    Capture($"{suffix}-3-errors", w =>
    {
        var f = ProfileFile.Load(File.ReadAllText(hero));
        // One of each, because the two are not treated the same: a word where
        // a device setting's number goes is an error and blocks the install,
        // an input name the device does not know is a warning and does not.
        f.SetCell(4, 0, "mouse_speed");
        f.SetCell(4, 2, "fast");
        f.SetCell(5, 2, "left_sip");
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

// The same shot at a window size of its own, for the layouts that only go
// wrong when there is less room than the default.
void CaptureSized(string name, int width, int height, Action<MainWindow> setup)
{
    var win = new MainWindow { Width = width, Height = height };
    win.Show();
    Dispatcher.UIThread.RunJobs();
    setup(win);
    win.UpdateLayout();
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
