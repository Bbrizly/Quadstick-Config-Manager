using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace QuadStick.App;

// A narrated first-run tour. It is a full-window overlay appended as the last
// child of RootPanel (a sibling AFTER ZoomHost) so it is never scaled by the
// interface-zoom transform and always covers the whole window. The tour drives
// the app itself, so the overlay swallows every click behind it; the user only
// presses the callout's Back / Skip / Next buttons.
//
// Data safety: the tour must never touch the user's real profile. It only ever
// runs on a throwaway template it creates at the "Your QuadStick" step, and it
// always discards that scratch file on teardown (Finish or Skip).
public partial class MainWindow
{
    Grid? _tourOverlay;
    Canvas _tourCanvas = null!;
    Border _tourRing = null!;
    Border[] _tourDim = null!;
    Border _tourCallout = null!;
    TextBlock _tourStep = null!, _tourTitle = null!, _tourBody = null!;
    Button _tourBack = null!, _tourSkip = null!, _tourNext = null!;
    int _tourIndex;

    (string Title, string Body, Action Setup, Func<Control?> Target)[] _tourSteps = null!;

    // Fires once from the constructor's Opened event on a genuine first run.
    void StartTutorialOnce(object? s, EventArgs e)
    {
        Opened -= StartTutorialOnce;
        StartTutorial();
    }

    public void StartTutorial()
    {
        // Never discard a user's unsaved work to run the tour.
        if (_file is { Dirty: true }) { _ = ConfirmThenStartAsync(); return; }
        BeginTutorial();
    }

    async Task ConfirmThenStartAsync()
    {
        if (await ConfirmLeaveAsync()) BeginTutorial();
    }

    void BeginTutorial()
    {
        _file = null;            // the tour runs only on a throwaway template it makes itself
        ShowHome();
        EnsureTutorialOverlay();
        _tourIndex = 0;
        _tourOverlay!.IsVisible = true;
        ShowTourStep();
    }

    void EndTutorial()
    {
        _settings.TutorialSeen = true;
        Settings.Save(_settings);
        if (_tourOverlay is not null) _tourOverlay.IsVisible = false;
        _file = null;            // always discard the scratch template
        ShowHome();
    }

    void EnsureTutorialOverlay()
    {
        if (_tourOverlay is not null) return;

        _tourSteps = new (string, string, Action, Func<Control?>)[]
        {
            ("Welcome",
             "This shows you how to make and install a profile. You can skip anytime.",
             () => ShowHome(),
             () => null),
            ("Appearance",
             "Set light or dark to suit your eyes.",
             () => { },
             () => AppearancePicker),
            ("New profile",
             "Every profile starts from a template.",
             () => { },
             () => HomeNewButton),
            ("Your QuadStick",
             "This is your QuadStick. Each part is a control you can map.",
             () => { if (_file is null) NewFromTemplate(); SetDeviceView(true); },
             () => DeviceViewButton),
            ("Pick a part",
             "Pick a part to see and change what it does.",
             () => SelectZoneForPreview("joystick"),
             () => ZoneDetailPanel),
            ("Save",
             "Save your work to a file.",
             () => { },
             () => SaveButton),
            ("Install",
             "When it's ready, send it to your QuadStick.",
             () => { },
             () => InstallButton),
            ("Done",
             "You can replay this anytime from Settings ▸ Help.",
             () => { },
             () => null),
        };

        // 1) Input blocker: a Transparent (not null) background is hit-test
        // visible, so it swallows clicks meant for the app behind the tour.
        var blocker = new Border { Background = Brushes.Transparent };

        // 2) Dim + ring canvas. Four dim strips frame the target and leave it
        // bright; the accent ring outlines the target itself.
        _tourCanvas = new Canvas();
        _tourDim = new Border[4];
        for (int i = 0; i < 4; i++)
        {
            _tourDim[i] = new Border { Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) };
            _tourCanvas.Children.Add(_tourDim[i]);
        }
        _tourRing = new Border
        {
            BorderThickness = new Thickness(3),
            Background = null,
            CornerRadius = new CornerRadius(6),
        };
        BindBrush(_tourRing, Border.BorderBrushProperty, "Accent");
        _tourCanvas.Children.Add(_tourRing);

        // 3) Callout card, docked bottom-centre.
        _tourStep = new TextBlock { FontSize = Size("SmallSize") };
        BindBrush(_tourStep, TextBlock.ForegroundProperty, "TextSecondary");
        _tourTitle = new TextBlock { FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        BindBrush(_tourTitle, TextBlock.ForegroundProperty, "TextPrimary");
        _tourBody = new TextBlock { FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        BindBrush(_tourBody, TextBlock.ForegroundProperty, "TextPrimary");

        _tourBack = new Button { Content = "Back" };
        AutomationProperties.SetName(_tourBack, "Back to the previous step");
        _tourBack.Click += (_, _) => { if (_tourIndex > 0) { _tourIndex--; ShowTourStep(); } };

        _tourSkip = new Button { Content = "Skip", IsCancel = true };
        AutomationProperties.SetName(_tourSkip, "Skip the tutorial");
        _tourSkip.Click += (_, _) => EndTutorial();

        _tourNext = new Button { Content = "Next", Classes = { "primary" }, IsDefault = true };
        AutomationProperties.SetName(_tourNext, "Next step");
        _tourNext.Click += (_, _) =>
        {
            if (_tourIndex >= _tourSteps.Length - 1) EndTutorial();
            else { _tourIndex++; ShowTourStep(); }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _tourBack, _tourSkip, _tourNext },
        };
        var stack = new StackPanel
        {
            Spacing = 12,
            Children = { _tourStep, _tourTitle, _tourBody, buttons },
        };
        _tourCallout = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 32),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            MaxWidth = 520,
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #40000000"),
            Child = stack,
        };
        BindBrush(_tourCallout, Border.BackgroundProperty, "Surface");
        BindBrush(_tourCallout, Border.BorderBrushProperty, "SurfaceBorder");

        // The callout is a live region so screen readers announce each step.
        AutomationProperties.SetLiveSetting(_tourCallout, AutomationLiveSetting.Polite);

        _tourOverlay = new Grid { IsVisible = false, Children = { blocker, _tourCanvas, _tourCallout } };
        RootPanel.Children.Add(_tourOverlay); // last child: sits above (and outside) the scaled content

        // Motion is the reduce-motion toggle's one observable effect: a gentle
        // fade per step when motion is allowed, nothing when it is reduced.
        RefreshTourMotion();

        // Keep the spotlight aligned with its target as the window resizes.
        SizeChanged += (_, _) => { if (_tourOverlay?.IsVisible == true) PositionSpotlight(); };
    }

    void ShowTourStep()
    {
        var step = _tourSteps[_tourIndex];
        step.Setup();

        _tourStep.Text = $"Step {_tourIndex + 1} of {_tourSteps.Length}";
        _tourTitle.Text = step.Title;
        _tourBody.Text = step.Body;
        _tourBack.IsEnabled = _tourIndex > 0;
        bool last = _tourIndex == _tourSteps.Length - 1;
        _tourNext.Content = last ? "Finish" : "Next";

        // The live region reads the whole step; keep its Name in sync.
        AutomationProperties.SetName(_tourCallout, $"{step.Title}. {step.Body}");

        if (!_reduceMotion) _tourCallout.Opacity = 0;

        // Position the spotlight only after the step's Setup has laid out, so
        // the target's on-screen rect is final.
        Dispatcher.UIThread.Post(() =>
        {
            PositionSpotlight();
            if (!_reduceMotion) _tourCallout.Opacity = 1;
            _tourNext.Focus(); // land keyboard/screen-reader users on the callout
        }, DispatcherPriority.Loaded);
    }

    void PositionSpotlight()
    {
        if (_tourOverlay is null) return;
        double cw = _tourCanvas.Bounds.Width, ch = _tourCanvas.Bounds.Height;

        var target = _tourSteps[_tourIndex].Target();
        // TranslatePoint maps from the (scaled) target into the un-scaled
        // canvas, so it already accounts for the interface-zoom transform.
        Point? origin = target?.TranslatePoint(new Point(0, 0), _tourCanvas);

        if (target is null || origin is not { } p)
        {
            // No target: dim the whole window, hide the ring.
            SetRect(_tourDim[0], 0, 0, cw, ch);
            SetRect(_tourDim[1], 0, 0, 0, 0);
            SetRect(_tourDim[2], 0, 0, 0, 0);
            SetRect(_tourDim[3], 0, 0, 0, 0);
            _tourRing.IsVisible = false;
            return;
        }

        const double pad = 6;
        double left = p.X - pad, top = p.Y - pad;
        double w = target.Bounds.Width * _uiScale + pad * 2, h = target.Bounds.Height * _uiScale + pad * 2;

        _tourRing.IsVisible = true;
        SetRect(_tourRing, left, top, w, h);
        // Four strips around the bright target rectangle.
        SetRect(_tourDim[0], 0, 0, cw, top);                       // above
        SetRect(_tourDim[1], 0, top + h, cw, ch - (top + h));      // below
        SetRect(_tourDim[2], 0, top, left, h);                     // left
        SetRect(_tourDim[3], left + w, top, cw - (left + w), h);   // right
    }

    static void SetRect(Border b, double left, double top, double width, double height)
    {
        Canvas.SetLeft(b, left);
        Canvas.SetTop(b, top);
        b.Width = Math.Max(0, width);
        b.Height = Math.Max(0, height);
    }

    // Lets a runtime Reduce Motion change (Settings ▸ Advanced) take effect
    // on the already-built overlay, not just on the next tour it starts.
    void RefreshTourMotion()
    {
        if (_tourOverlay is null) return; // overlay not built yet
        _tourCallout.Transitions = _reduceMotion ? null
            : new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) } };
    }
}
