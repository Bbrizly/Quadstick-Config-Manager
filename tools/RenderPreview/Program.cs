using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using QuadStick.App;
using QuadStick.Format;

// Renders MainWindow to PNGs for docs, and the appearance gallery beside them,
// so a change to Style.cs or Palette.cs can be looked at in both themes without
// launching anything. Usage: RenderPreview [out-dir] [corpus-dir]

var outDir = args.Length > 0 ? args[0] : "/tmp/qscm-renders";
var corpus = args.Length > 1 ? args[1] : "tests/QuadStick.Format.Tests/corpus";
Directory.CreateDirectory(outDir);

// Fake a profile library so the home screen has cards to show.
var lib = Directory.CreateTempSubdirectory("qscm-lib-").FullName;
File.Copy(Path.Combine(corpus, "gta-mode1.csv"), Path.Combine(lib, "gta.csv"));
File.Copy(Path.Combine(corpus, "default.csv"), Path.Combine(lib, "rocket-league.csv"));
MainWindow.LibraryDir = lib;

Environment.SetEnvironmentVariable("QSCM_TELEMETRY", "0");   // screenshots never send

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
