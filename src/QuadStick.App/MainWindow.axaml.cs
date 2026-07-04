using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using QuadStick.Format;

namespace QuadStick.App;

public partial class MainWindow : Window
{
    // Bind any brush property to a theme token so it repaints on theme change.
    // Never resolve+assign a concrete brush for a themed color: that freezes it.
    static void BindBrush(Control target, AvaloniaProperty property, string tokenKey) =>
        target[!property] = new DynamicResourceExtension(tokenKey + "Brush");

    // Type scale doesn't change with theme, so a one-time resource read is fine
    // here (same reasoning as the icon Data lookup below).
    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    enum StatusKind { Ready, Info, Warning, Error }

    static Control StatusChip(StatusKind kind, string text, bool plainDot = false)
    {
        var (iconKey, tokenKey) = kind switch
        {
            StatusKind.Ready   => ("IconCheck",   "Success"),
            StatusKind.Warning => ("IconWarning", "Warning"),
            StatusKind.Error   => ("IconError",   "Error"),
            _                  => ("IconChevron", "TextSecondary"),
        };
        // A neutral "not connected" state reads better as a simple hollow dot
        // than a chevron glyph (which looked like a stray ">").
        Control icon;
        if (plainDot)
        {
            var dot = new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6),
                BorderThickness = new Avalonia.Thickness(2), Background = Brushes.Transparent };
            BindBrush(dot, Border.BorderBrushProperty, tokenKey);
            icon = dot;
        }
        else
        {
            // Data is a one-time resource read (geometry doesn't change with theme);
            // Foreground MUST be a dynamic binding so the color follows the theme.
            var pathIcon = new PathIcon { Width = 16, Height = 16,
                Data = (Geometry)Application.Current!.FindResource(iconKey)! };
            BindBrush(pathIcon, IconElement.ForegroundProperty, tokenKey);
            icon = pathIcon;
        }
        var label = new TextBlock { Text = text, FontSize = Size("BodySize"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        BindBrush(label, TextBlock.ForegroundProperty, tokenKey);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { icon, label } };
    }

    ProfileFile? _file;
    string? _savePath;          // where Save writes; null until saved or opened from a path
    int _sheetIndex;
    bool _deviceView = true;    // the visual mapper is the default face of the editor
    string? _selectedZone;
    // Device View shows friendly words ("soft sip") by default; the Labels
    // toggle flips every input/output/function name to the raw token the List
    // View and the CSV use ("mp_left_sip_soft"), so the two views speak the
    // same vocabulary when a power user wants it.
    bool _friendlyLabels = true;
    QsModel _model;
    AppSettings _settings = Settings.Load();
    double _uiScale = 1.0;
    bool _reduceMotion;

    enum QsModel { FPS, Original, Singleton }
    static readonly string[] ModelNames = { "QuadStick FPS", "QuadStick Original", "QuadStick Singleton" };

    void SaveModel() { _settings.Model = _model.ToString(); Settings.Save(_settings); }

    readonly Dictionary<string, Border> _cellBorders = new();
    readonly Dictionary<string, Button> _zoneButtons = new(); // Device View zone id -> its button, for focus management
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    const string DefaultNewName = "mygame.csv";

    public static string LibraryDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuadStick Profiles");

    static readonly HashSet<string> JoystickDirs = new(StringComparer.OrdinalIgnoreCase)
    { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    static readonly List<string> OutputSuggestions = Vocab.KnownOutputs.OrderBy(x => x).ToList();
    static readonly List<string> OutputSuggestionsPs = Vocab.OutputsPs3.OrderBy(x => x).ToList();
    static readonly List<string> OutputSuggestionsXbox = Vocab.OutputsXbox.OrderBy(x => x).ToList();
    static readonly List<string> FunctionSuggestions = Vocab.FunctionArity.Keys.OrderBy(x => x).ToList();
    static readonly List<string> InputSuggestions = Vocab.Inputs.OrderBy(GroupRank).ThenBy(x => x).ToList();
    static readonly List<string> NoSuggestions = new();

    // A3 of each sheet names the output convention ("PlayStation Outputs" /
    // "XBox Outputs"). Suggest the matching set; union when the label is generic.
    static List<string> OutputSuggestionsFor(ModeSheet s)
    {
        var label = s.HeaderLabel;
        if (label.Contains("xbox", StringComparison.OrdinalIgnoreCase)) return OutputSuggestionsXbox;
        if (label.Contains("playstation", StringComparison.OrdinalIgnoreCase)) return OutputSuggestionsPs;
        return OutputSuggestions;
    }

    // The sheet's color language, familiar to every QuadStick user:
    // yellow = outputs, pink = function, blue = inputs. Keys into the theme's
    // <Key>Brush DynamicResource (Palette.cs / Theme.cs), not fixed colors.
    const string OutputTint = "OutputTint";
    const string FunctionTint = "FunctionTint";
    const string InputTint = "InputTint";

    // Group inputs the way users think of the hardware, not alphabetically.
    static int GroupRank(string input) => input switch
    {
        _ when input.StartsWith("mp_") => 0,                                         // mouthpiece
        "lip" or "lip_soft" => 1,                                                    // lip switch
        _ when input.StartsWith("right_") => 2,                                      // side tube
        "left" or "right" or "up" or "down" or "any_direction" or "center" => 3,     // joystick
        _ when JoystickDirs.Contains(input) || JoystickDirs.Contains(input.Replace("_inner", "")) => 3,
        _ when input.StartsWith("usb_") => 5,
        _ when input.StartsWith("digital_") => 6,
        _ => 4,
    };

    public MainWindow()
    {
        InitializeComponent();
        var v = typeof(MainWindow).Assembly.GetName().Version;
        HomeVersionText.Text = $"v{v?.Major}.{v?.Minor}.{v?.Build} · MIT · not affiliated with QuadStick";

        if (_settings.RememberWindow)
        {
            if (_settings.WinW is { } winW && _settings.WinH is { } winH)
            {
                Width = Math.Max(winW, MinWidth);
                Height = Math.Max(winH, MinHeight);
            }
            if (_settings.WinX is { } winX && _settings.WinY is { } winY)
                Position = new PixelPoint((int)winX, (int)winY);
        }
        RootPanel.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty) UpdateScaleSize(); };
        Opened += (_, _) => ClampToScreen();

        HomeNewButton.Click += (_, _) => NewFromTemplate();
        HomeOpenButton.Click += async (_, _) => await OpenAsync();
        HomeHelpButton.Click += (_, _) => ShowHelp();
        ImportButton.Click += async (_, _) => await ImportAsync();

        // Empty-library state offers the same three actions as the Start
        // cards above, so an empty library is never a dead end.
        LibraryEmptyNewButton.Click += (_, _) => NewFromTemplate();
        LibraryEmptyOpenButton.Click += async (_, _) => await OpenAsync();
        LibraryEmptyImportButton.Click += async (_, _) => await ImportAsync();

        HomeButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) ShowHome(); };
        Closing += async (_, e) =>
        {
            if (_file is not { Dirty: true } || _closeConfirmed) return;
            e.Cancel = true;
            if (await ConfirmLeaveAsync()) { _closeConfirmed = true; Close(); }
        };
        Closing += (_, _) =>
        {
            if (!_settings.RememberWindow) return;
            _settings.WinW = Width;
            _settings.WinH = Height;
            _settings.WinX = Position.X;
            _settings.WinY = Position.Y;
            Settings.Save(_settings);
        };
        FileNameBox.LostFocus += (_, _) => CommitFileName();
        FileNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitFileName(); };
        SaveButton.Click += async (_, _) => await SaveAsync();
        UndoButton.Click += (_, _) => UndoEdit();
        InstallButton.Click += async (_, _) => await RunInstallFlowAsync();
        HelpButton.Click += (_, _) => ShowHelp();
        AddRowButton.Click += (_, _) => AddRow();
        SheetPicker.SelectionChanged += (_, _) =>
        {
            if (SheetPicker.SelectedIndex >= 0 && _file != null)
            { _sheetIndex = SheetPicker.SelectedIndex; _selectedZone = null; RefreshEditor(); }
        };

        // Plain-language explainers, shown as dismissable popups so the answer
        // is one click away and never clutters the editing surface.
        ModeHelpButton.Click += (_, _) => ShowInfoFlyout(ModeHelpButton, "What is a mode?",
            "A mode is one full layout of your inputs. A profile can hold several and you switch "
            + "between them while playing, for example a walking layout and a driving layout. To "
            + "switch modes in the game, sip or puff the side tube, or map increment_mode / "
            + "decrement_mode to an input.\n\n"
            + "Most profiles have just one mode, so this list often shows a single entry. That is normal.");
        DeviceHelpButton.Click += (_, _) => ShowInfoFlyout(DeviceHelpButton, "Using device view",
            "Click any part of the QuadStick to see and change what it does in this mode. The number "
            + "on each part is how many game buttons it presses. Parts your model does not have are "
            + "dimmed.\n\n" + ModelDescription);

        ProblemsToggle.Click += (_, _) => ToggleProblems();

        // Selecting a problem copies it, so users can paste it into a bug
        // report or a forum post without retyping. It also jumps focus to
        // the offending cell so the user can fix it right away.
        IssuesList.SelectionChanged += async (_, _) =>
        {
            if (IssuesList.SelectedItem is TextBlock { Text.Length: > 0 } tb && Clipboard is { } cb)
            {
                await cb.SetTextAsync(tb.Text);
                Status("Problem copied to the clipboard.", StatusKind.Info);
                if (tb.Tag is Issue issue) FocusIssueCell(issue);
                IssuesList.SelectedIndex = -1; // allow copying the same one again
            }
        };
        FixFirstButton.Click += (_, _) =>
        {
            var firstError = _file?.Issues.FirstOrDefault(i => i.Severity == Severity.Error);
            if (firstError is null) { Status("No errors to fix.", StatusKind.Ready); return; }
            FocusIssueCell(firstError);
        };

        DeviceViewButton.Click += (_, _) => SetDeviceView(true);
        ListViewButton.Click += (_, _) => SetDeviceView(false);
        LabelStyleButton.Click += (_, _) => ToggleLabelStyle();
        UpdateLabelStyleButton();
        _model = Enum.TryParse<QsModel>(_settings.Model, out var savedModel) ? savedModel : QsModel.FPS;
        ModelPicker.ItemsSource = ModelNames;
        ModelPicker.SelectedIndex = (int)_model;
        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (_pickerSyncing) return;
            if (ModelPicker.SelectedIndex < 0) return;
            _model = (QsModel)ModelPicker.SelectedIndex;
            SaveModel();
            if (_deviceView) { _selectedZone = null; RefreshEditor(); }
        };

        var savedTheme = _settings.Theme;
        AppearancePicker.ItemsSource = new[] { "System", "Light", "Dark" };
        AppearancePicker.SelectedIndex = savedTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        // ApplyTheme sets SelectedIndex back to the same value on the way out,
        // which does not re-fire SelectionChanged, so this can't loop.
        AppearancePicker.SelectionChanged += (_, _) => ApplyTheme((string)AppearancePicker.SelectedItem!);

        SettingsButton.Click += (_, _) => new SettingsWindow(this).ShowDialog(this);

        // Ctrl (Windows/Linux) or Cmd (macOS) shortcuts, plus the bare F1 help
        // key. Ctrl-combos are safe to fire even while a field has focus
        // (that's how Ctrl+S already behaved, and how every other app
        // treats Ctrl+shortcuts); a *bare* letter key is not, since it would
        // steal a keystroke out of whatever the user is typing (e.g. an
        // un-modified "i" over RunInstallFlowAsync mid-edit). Only the modifier-free
        // case needs the typing guard.
        KeyDown += (_, e) =>
        {
            // The tutorial overlay owns the keyboard while it's up: its Next/Skip
            // (Enter/Esc) still work, but app shortcuts like Ctrl+O must not fire
            // behind it — Ctrl+O would swap in a real profile that teardown then
            // discards. Returning without Handled leaves Enter/Esc to the callout.
            if (_tourOverlay?.IsVisible == true) return;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                if (e.Key == Key.F1 && e.Source is not (TextBox or AutoCompleteBox))
                { ShowHelp(); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                case Key.O: _ = OpenAsync(); e.Handled = true; break;
                case Key.S: _ = SaveAsync(); e.Handled = true; break;
                case Key.N: NewFromTemplate(); e.Handled = true; break; // uses DefaultNewName
                case Key.Z: UndoEdit(); e.Handled = true; break;
                case Key.I: _ = RunInstallFlowAsync(); e.Handled = true; break;
                case Key.D: SetDeviceView(!_deviceView); e.Handled = true; break;
                case Key.H: ShowHelp(); e.Handled = true; break;
            }
        };

        _reduceMotion = _settings.ReduceMotion;
        ApplyInterfaceScale(_settings.InterfaceScalePercent);
        ShowHome();
        if (!_settings.TutorialSeen) Opened += StartTutorialOnce;
    }

    static readonly int[] ValidScalePercents = { 100, 125, 150, 200 };

    public void ApplyInterfaceScale(int pct)
    {
        if (Array.IndexOf(ValidScalePercents, pct) < 0) pct = 100;
        _uiScale = pct / 100.0;
        ZoomHost.LayoutTransform = _uiScale == 1.0 ? null : new ScaleTransform(_uiScale, _uiScale);
        EnsureWindowFitsScale();
        UpdateScaleSize();
    }

    // Bigger scale needs a bigger window or the fixed-height Problems dock
    // crowds the editor. Grow (never shrink) toward a comfortable size, capped
    // to the screen. Skipped until the window is on screen, so the saved-size
    // restore in the constructor wins at startup.
    void EnsureWindowFitsScale()
    {
        if (!IsVisible || WindowState != WindowState.Normal) return;
        var screen = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        if (screen is null) return;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var wantW = Math.Min(1000 * _uiScale, screen.WorkingArea.Width / scaling);
        var wantH = Math.Min(720 * _uiScale, screen.WorkingArea.Height / scaling);
        if (Width < wantW) Width = wantW;
        if (Height < wantH) Height = wantH;
    }

    // A remembered position can land off-screen after a monitor is unplugged.
    // For a mouth-operated app a lost window is very hard to recover, so pull
    // it back onto a real screen once we know the actual monitor layout.
    void ClampToScreen()
    {
        if (Screens is not { } screens) return;
        if (screens.All.Any(s => s.WorkingArea.Contains(Position))) return;
        var wa = (screens.ScreenFromWindow(this) ?? screens.Primary)?.WorkingArea;
        if (wa is { } r) Position = new PixelPoint(r.X + 40, r.Y + 40);
    }

    void UpdateScaleSize()
    {
        if (RootPanel.Bounds is { Width: > 0, Height: > 0 } b)
        {
            ScaleContent.Width = b.Width / _uiScale;
            ScaleContent.Height = b.Height / _uiScale;
        }
    }

    // ---- Settings window API: SettingsWindow.cs calls these so every
    // setting applies live and persists through the same single source of
    // truth (_settings) the rest of the window already uses. ----
    public AppSettings CurrentSettings => _settings;
    public void PersistSettings() => Settings.Save(_settings);
    public static IReadOnlyList<string> ModelDisplayNames => ModelNames;
    public double UiScale => _uiScale;

    // Wrap a window's content so it scales with the app's interface-size setting.
    public static Control ZoomWrap(Control content, double scale) =>
        scale == 1.0 ? content
        : new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };

    bool _pickerSyncing; // stops the header/settings pickers re-triggering each other

    public void ApplyTheme(string choice)
    {
        if (_pickerSyncing) return;
        _pickerSyncing = true;
        try
        {
            QuadStick.App.Theme.Apply(choice);
            _settings.Theme = choice;
            Settings.Save(_settings);
            AppearancePicker.SelectedIndex = choice switch { "Light" => 1, "Dark" => 2, _ => 0 };
        }
        finally { _pickerSyncing = false; }
    }

    public void SetInterfaceScale(int pct)
    {
        _settings.InterfaceScalePercent = pct;
        Settings.Save(_settings);
        ApplyInterfaceScale(pct);
    }

    public void SetReduceMotion(bool v)
    {
        _reduceMotion = v;
        _settings.ReduceMotion = v;
        Settings.Save(_settings);
        RefreshTourMotion();
    }

    public void SetDefaultModel(int index)
    {
        if (_pickerSyncing || index < 0 || index >= ModelNames.Length) return;
        _pickerSyncing = true;
        try
        {
            _model = (QsModel)index;
            ModelPicker.SelectedIndex = index;
            SaveModel();
            if (_deviceView) { _selectedZone = null; RefreshEditor(); }
        }
        finally { _pickerSyncing = false; }
    }

    public void ResetSettings()
    {
        _settings = new AppSettings();
        Settings.Save(_settings);
        QuadStick.App.Theme.Apply(_settings.Theme);
        AppearancePicker.SelectedIndex = 0;
        _reduceMotion = _settings.ReduceMotion;
        RefreshTourMotion();
        _model = QsModel.FPS;
        ModelPicker.SelectedIndex = 0;
        ApplyInterfaceScale(_settings.InterfaceScalePercent);
    }

    // Small public wrapper: ConfirmAsync itself is private, and the Settings
    // window's Reset button needs the same confirm-dialog idiom.
    public Task<bool> ConfirmResetAsync() => ConfirmAsync("Reset all settings?",
        "Appearance, interface size, and the rest go back to their defaults.");

    void ShowHome()
    {
        HomeView.IsVisible = true;
        EditorView.IsVisible = false;
        RefreshHomeCards();
        HomeNewButton.Focus();
    }

    void ShowEditor()
    {
        HomeView.IsVisible = false;
        EditorView.IsVisible = true;
        HomeButton.Focus();
    }

    // Switches between Device View and List View, keeping keyboard focus on
    // the new view's first interactive control instead of dropping it.
    void SetDeviceView(bool device)
    {
        _deviceView = device;
        RefreshEditor();
        if (device)
        {
            if (_zoneButtons.TryGetValue("joystick", out var joystickBtn)) joystickBtn.Focus();
        }
        else if (CurrentSheet?.Bindings.FirstOrDefault()?.Row is int firstRow
                 && _cellBorders.TryGetValue($"A{firstRow}", out var border))
        { border.BringIntoView(); (border.Child as AutoCompleteBox)?.Focus(); }
        else AddRowButton.Focus();
    }

    // Flip Device View between plain-English words and the raw list-view/CSV
    // token names, and rebuild so every dropdown label follows suit.
    void ToggleLabelStyle()
    {
        _friendlyLabels = !_friendlyLabels;
        UpdateLabelStyleButton();
        if (_deviceView && CurrentSheet?.Type == SheetType.ProfileName)
        { BuildDeviceView(); BuildZoneDetail(); }
    }

    void UpdateLabelStyleButton()
    {
        LabelStyleButton.Content = _friendlyLabels ? "Words: Plain English" : "Words: List names";
        AutomationProperties.SetName(LabelStyleButton, _friendlyLabels
            ? "Words are shown in plain English. Switch to the raw list-view names."
            : "Words are shown as raw list-view names. Switch to plain English.");
    }

    void RefreshHomeCards()
    {
        LibraryCards.Children.Clear();
        var libraryFiles = Directory.Exists(LibraryDir)
            ? Directory.GetFiles(LibraryDir, "*.csv").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        LibraryEmptyPanel.IsVisible = libraryFiles.Length == 0;
        foreach (var path in libraryFiles)
            LibraryCards.Children.Add(ProfileCard(path, onDevice: false));

        DeviceCards.Children.Clear();
        var deviceFiles = Device.FindCandidates()
            .SelectMany(root => Directory.GetFiles(root, "*.csv"))
            .OrderBy(Path.GetFileName)
            .ToArray();
        DeviceEmptyText.IsVisible = deviceFiles.Length == 0;
        foreach (var path in deviceFiles)
            DeviceCards.Children.Add(ProfileCard(path, onDevice: true));
    }

    Control ProfileCard(string path, bool onDevice)
    {
        var name = Path.GetFileName(path);
        string subtitle;
        try
        {
            var doc = Parser.Parse(File.ReadAllText(path)).Doc;
            var bindings = doc.Sheets.Sum(s => s.Bindings.Count);
            subtitle = $"{doc.Sheets.Count} mode sheet(s), {bindings} binding(s)";
        }
        catch { subtitle = "Could not read this file"; }
        if (onDevice && name.Equals("default.csv", StringComparison.OrdinalIgnoreCase))
            subtitle += " · the device's fallback file";

        var card = new Button { Classes = { "card" } };
        AutomationProperties.SetName(card,
            $"Open {name}, {subtitle}{(onDevice ? ", stored on the QuadStick" : ", in your profile library")}");
        card.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = name, FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold },
                new TextBlock { Text = subtitle, Classes = { "cardsub" } },
            },
        };
        card.Click += (_, _) =>
        {
            try { OpenInEditor(ProfileFile.Load(File.ReadAllText(path)), path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status($"Could not open {name}: {ex.Message}", StatusKind.Error); ShowEditor(); }
        };
        return card;
    }

    void NewFromTemplate() => OpenInEditor(ProfileFile.NewFromTemplate(DefaultNewName), savePath: null);

    void OpenInEditor(ProfileFile file, string? savePath)
    {
        _file = file;
        _savePath = savePath;
        _sheetIndex = 0;
        SheetPicker.ItemsSource = file.Document.Sheets
            .Select((s, i) => $"{i + 1}: {(s.ModeName.Length > 0 ? s.ModeName : s.Type.ToString())}")
            .ToList();
        SheetPicker.SelectedIndex = 0;
        FileNameBox.Text = file.Document.CsvFileName ?? "";
        var headerName = file.Document.HeaderName;
        Title = "QuadStick Config Manager (unofficial) - "
            + (headerName.Length > 0 ? $"{headerName} ({file.Document.CsvFileName})" : file.Document.CsvFileName ?? "untitled");
        _selectedZone = null;
        ShowEditor();
        RefreshEditor(); // RefreshIssues inside sets the status line
    }

    sealed record Zone(string Id, string Title, string Display, string DefaultInput, string Blurb);

    static readonly Zone[] AllZones =
    {
        new("joystick", "Joystick", "Joystick", "up",
            "Moving the whole mouthpiece with your mouth works like a joystick. Up, down, left, right, and the 8 compass directions can each press something."),
        new("mp_left", "Left mouthpiece hole", "Left", "mp_left_sip",
            "Sip or puff on the left mouthpiece hole. A gentle sip or puff can do something different (the soft variants)."),
        new("mp_center", "Center mouthpiece hole", "Center", "mp_center_sip",
            "Sip or puff on the center mouthpiece hole. A gentle sip or puff can do something different (the soft variants)."),
        new("mp_right", "Right mouthpiece hole", "Right", "mp_right_sip",
            "Sip or puff on the right mouthpiece hole. A gentle sip or puff can do something different (the soft variants)."),
        new("combo", "Hole combos", "Combos", "mp_left_center_sip",
            "Two or more holes used at the same time, or all three together. Good for actions you never want to trigger by accident."),
        new("side", "Side tube", "Side tube", "right_sip",
            "Sip or puff on the side tube. A long hard sip here normally switches profiles, but you can map it too."),
        new("lip", "Lip switch", "Lip switch", "lip",
            "Press the lip switch or sensor with your lip. Often used for the fire button."),
        new("jacks", "Switch jacks", "Switch jacks", "digital_in_1",
            "External adaptive switches plugged into the 3.5 mm jacks on the back of the QuadStick (up to 4)."),
        new("other", "USB devices", "USB devices", "usb_1_button_1",
            "Extra USB joysticks or controllers plugged into the QuadStick's USB-A port."),
        new("unset", "No input yet", "No input yet", "",
            "Rows that press a game button but have nothing triggering them yet. Give each one an input, or delete it."),
    };

    static string ZoneOf(string input) => input switch
    {
        "" => "unset",
        _ when input.StartsWith("mp_left_center") || input.StartsWith("mp_right_center")
            || input.StartsWith("mp_left_right") || input.StartsWith("mp_triple") => "combo",
        _ when input.StartsWith("mp_left") => "mp_left",
        _ when input.StartsWith("mp_center") => "mp_center",
        _ when input.StartsWith("mp_right") => "mp_right",
        "right_sip" or "right_puff" or "right_sip_soft" or "right_puff_soft" => "side",
        "lip" => "lip",
        _ when input.StartsWith("digital_in") => "jacks",
        "left" or "right" or "up" or "down" or "any_direction" or "center" => "joystick",
        _ when JoystickDirs.Contains(input) || JoystickDirs.Contains(input.Replace("_inner", "")) => "joystick",
        _ => "other",
    };

    // What each model actually has, per quadstick.com. FPS and Original share
    // the same inputs; the FPS difference is joystick precision, not mapping.
    string ModelDescription => _model switch
    {
        QsModel.Singleton => "Singleton: a single sip/puff tube on the joystick. Uses sip and puff patterns plus joystick movement.",
        QsModel.Original => "Original: 3-hole mouthpiece, side tube, lip switch, rear jacks. Same inputs as the FPS.",
        _ => "FPS: 3-hole mouthpiece, side tube, lip sensor, rear jacks. More precise joystick than the Original.",
    };

    // "mp_left_puff_soft" reads as "soft puff" on the Left hole's own card.
    static string ShortInput(Zone z, Binding b)
    {
        var input = b.Inputs.Count > 0 ? b.Inputs[0] : "";
        if (input.Length == 0) return "(no input)";
        var extra = b.Inputs.Count > 1 ? $" +{b.Inputs.Count - 1}" : "";
        return StripInput(input, z.Id) + extra;
    }

    // The friendly short form of one input token, scoped to the part it lives
    // on: on the Left hole "mp_left_puff_soft" becomes "soft puff". Shared by
    // ShortInput and the Device View dropdown labels.
    static string StripInput(string input, string zoneId)
    {
        if (input.Length == 0) return "(no input)";
        var s = input;
        foreach (var prefix in new[] { "mp_left_center_", "mp_right_center_", "mp_left_right_", "mp_triple_", "mp_left_", "mp_center_", "mp_right_", "right_" })
            if (zoneId is not ("joystick" or "other") && s.StartsWith(prefix)) { s = s[prefix.Length..]; break; }
        if (s.EndsWith("_soft")) s = "soft " + s[..^5];
        return s.Replace('_', ' ');
    }

    // Turn a raw token into plain words: "mouse_left_button" -> "Mouse left
    // button". Used for outputs and functions in friendly mode.
    static string Humanize(string token)
    {
        var s = (token ?? "").Trim();
        if (s.Length == 0) return s;
        s = s.Replace('_', ' ');
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // How an input/output/function token is shown in Device View: friendly
    // words, or the raw token exactly as List View and the CSV spell it.
    string InputLabel(string token, string zoneId) =>
        _friendlyLabels ? Humanize(StripInput(token, zoneId)) : token;
    string TokenLabel(string token) => _friendlyLabels ? Humanize(token) : token;

    Dictionary<string, List<Binding>> BindingsByZone()
    {
        var map = new Dictionary<string, List<Binding>>();
        foreach (var b in CurrentSheet?.Bindings ?? [])
        {
            var zones = b.Inputs.Count > 0
                ? b.Inputs.Select(ZoneOf).Distinct()
                : new[] { "unset" };
            foreach (var z in zones)
            {
                if (!map.TryGetValue(z, out var list))
                    map[z] = list = new();
                list.Add(b);
            }
        }
        return map;
    }

    IEnumerable<Zone> VisibleZones(Dictionary<string, List<Binding>> byZone) =>
        AllZones.Where(z => z.Id switch
        {
            // The Singleton has one mouthpiece tube: no left/right holes,
            // no combos, no separate side tube. Still show them if the
            // profile actually maps them, so nothing is ever hidden-but-live.
            "mp_left" or "mp_right" or "combo" or "side" =>
                _model != QsModel.Singleton || byZone.ContainsKey(z.Id),
            "other" or "unset" => byZone.ContainsKey(z.Id),
            _ => true,
        });

    void RefreshEditor()
    {
        bool device = _deviceView && CurrentSheet?.Type == SheetType.ProfileName;
        GridContainer.IsVisible = !device;
        DeviceContainer.IsVisible = device;
        DeviceViewButton.Classes.Set("primary", device);
        ListViewButton.Classes.Set("primary", !device);
        // The words toggle only changes Device View labels; List View already
        // shows the raw names, so hide it there rather than offer a dead control.
        LabelStyleButton.IsVisible = device;
        var connected = Device.FindCandidates().Count > 0;
        if (device)
        {
            // Device View is the signature surface: a header row above the
            // canvas repeats connection + mode so it reads as the primary
            // editor, not a secondary tab.
            DeviceHeaderStatus.Content = StatusChip(connected ? StatusKind.Ready : StatusKind.Info,
                connected ? "QuadStick connected" : "No QuadStick detected", plainDot: !connected);
            var modeName = CurrentSheet is { } cs ? (cs.ModeName.Length > 0 ? cs.ModeName : cs.Type.ToString()) : "";
            DeviceHeaderMode.Text = modeName.Length > 0 ? $"Mode: {modeName}" : "";
            BuildDeviceView(); BuildZoneDetail();
        }
        else RebuildRows();
        RefreshIssues();
    }

    void BuildDeviceView()
    {
        DeviceCanvas.Children.Clear();
        _zoneButtons.Clear();
        var byZone = BindingsByZone();
        var visible = VisibleZones(byZone).ToList();

        // ---- Main diagram, stacked to mirror the real device top to bottom:
        // the joystick on top, the round mouthpiece holes below it, then the
        // side tube + lip switch. Full width, so each row's parts can be large;
        // sized to fit the panel whole, so nothing here scrolls. ----
        var diagram = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };

        var joystick = ZoneButton(AllZones[0], byZone, 220, minHeight: 120);
        joystick.HorizontalAlignment = HorizontalAlignment.Center;
        diagram.Children.Add(joystick);

        var rightCol = diagram; // rows are appended straight down the stack

        // A WrapPanel so the round holes drop to a second row instead of
        // clipping when the window is narrow; on a normal window they sit in a
        // single row across the mouthpiece.
        var holes = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var z in visible.Where(z => z.Id.StartsWith("mp_")))
        {
            var hole = ZoneButton(z, byZone, 90, circle: true);
            hole.Margin = new Avalonia.Thickness(4);
            holes.Children.Add(hole);
        }
        if (holes.Children.Count > 0)
        {
            var mouthpieceBar = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(22),
                Padding = new Avalonia.Thickness(14, 12),
                BorderThickness = new Avalonia.Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Mouthpiece", FontWeight = FontWeight.Bold, FontSize = Size("SmallSize"),
                                        HorizontalAlignment = HorizontalAlignment.Center },
                        holes,
                    },
                },
            };
            BindBrush(mouthpieceBar, Border.BackgroundProperty, "SurfaceSubtle");
            BindBrush(mouthpieceBar, Border.BorderBrushProperty, "SurfaceBorder");
            rightCol.Children.Add(mouthpieceBar);
        }

        var sideRow = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var z in visible.Where(z => z.Id is "side" or "lip"))
            sideRow.Children.Add(ZoneButton(z, byZone, 200, minHeight: 108));
        if (sideRow.Children.Count > 0) rightCol.Children.Add(sideRow);

        DeviceCanvas.Children.Add(diagram);

        // ---- Secondary parts along the bottom: hole combos, switch jacks, USB,
        // then unmapped rows last so "No input yet" lands at the bottom right. ----
        var extras = new WrapPanel { Margin = new Avalonia.Thickness(0, 18, 0, 0) };
        void AddExtra(Zone z) { var t = ZoneButton(z, byZone, 210, minHeight: 108); t.Margin = new Avalonia.Thickness(0, 0, 12, 12); extras.Children.Add(t); }
        foreach (var z in visible.Where(z => z.Id is "combo" or "jacks" or "other")) AddExtra(z);
        foreach (var z in visible.Where(z => z.Id is "unset")) AddExtra(z);
        if (extras.Children.Count > 0) DeviceCanvas.Children.Add(extras);
    }

    // Which parts the selected model physically has. Zones the model lacks
    // still show when a profile maps them, but marked, so a profile made for
    // an FPS is never silently broken on a Singleton.
    bool ModelHasZone(string zoneId) =>
        _model != QsModel.Singleton
        || zoneId is not ("mp_left" or "mp_right" or "combo" or "side" or "lip" or "jacks");

    Control ZoneButton(Zone z, Dictionary<string, List<Binding>> byZone, double minWidth,
                       double minHeight = 84, bool circle = false)
    {
        byZone.TryGetValue(z.Id, out var bindings);
        int count = bindings?.Count ?? 0;
        bool foreign = !ModelHasZone(z.Id);
        bool selected = _selectedZone == z.Id;

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            // Circular holes use the short name so the label fits inside the ring.
            Text = circle ? z.Display : z.Title, FontWeight = FontWeight.Bold,
            FontSize = Size(circle ? "SmallSize" : "BodySize"),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // One clean status line — a count, never a truncated dump of bindings.
        // The full, editable list lives in the detail panel, opened on select.
        var countLabel = new TextBlock
        {
            Text = count == 0 ? "Not mapped" : count == 1 ? "1 mapping" : $"{count} mappings",
            FontSize = Size("SmallSize"), TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (count == 0) countLabel.Classes.Add("muted");
        else BindBrush(countLabel, TextBlock.ForegroundProperty, "AccentText");
        content.Children.Add(countLabel);

        if (foreign && !circle)
            content.Children.Add(new TextBlock
            {
                Text = "Not on your model", FontSize = Size("SmallSize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

        var btn = new ToggleButton
        {
            Classes = { "zone" }, MinWidth = minWidth, MinHeight = minHeight,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content,
            IsChecked = selected,
        };
        if (circle)
        {
            btn.Width = minWidth; btn.Height = minWidth;
            btn.CornerRadius = new Avalonia.CornerRadius(minWidth / 2);
            btn.Padding = new Avalonia.Thickness(2);
        }
        // Parts the model doesn't physically have are greyed out but still
        // reachable, so a profile built for another model can be cleaned up.
        if (foreign) btn.Opacity = 0.5;

        var spoken = count == 0
            ? "nothing mapped yet"
            : string.Join(", ", (bindings ?? new()).Take(4).Select(b => $"{ShortInput(z, b)} presses {b.Output}"));
        var warning = foreign ? $" Not available on your {ModelNames[(int)_model]}." : "";
        AutomationProperties.SetName(btn,
            $"{z.Title}. {(selected ? "Selected. " : "")}{count} mapping{(count == 1 ? "" : "s")}. {spoken}.{warning} Press Enter to edit.");
        btn.Click += (_, _) =>
        {
            _selectedZone = z.Id;
            BuildDeviceView(); BuildZoneDetail();
            // The click target no longer exists after the rebuild above;
            // refocus its replacement so keyboard/switch users aren't dropped.
            // IsChecked is re-derived from _selectedZone on rebuild, not from
            // ToggleButton's own toggle state, so re-clicking the selected
            // zone can't leave it unchecked with no selection.
            _zoneButtons.GetValueOrDefault(z.Id)?.Focus();
        };
        _zoneButtons[z.Id] = btn;
        return btn;
    }

    void BuildZoneDetail()
    {
        ZoneDetailPanel.Children.Clear();
        var zone = AllZones.FirstOrDefault(z => z.Id == _selectedZone);
        if (zone is null)
        {
            ZoneDetailPanel.Children.Add(new TextBlock
            {
                Text = "Nothing selected.\n\nPick a part of the QuadStick on the left to see what it does in this mode, change it, or map something new to it.",
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var byZone = BindingsByZone();
        byZone.TryGetValue(zone.Id, out var bindings);

        var zoneTitle = new TextBlock
        {
            Text = $"{zone.Title}  ·  {bindings?.Count ?? 0} mapping(s)",
            FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(zoneTitle, AutomationLiveSetting.Polite);
        ZoneDetailPanel.Children.Add(zoneTitle);
        ZoneDetailPanel.Children.Add(new TextBlock
        { Text = zone.Blurb, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });

        if (bindings is { Count: > 0 })
        {
            var zoneInputs = Vocab.Inputs.Where(i => ZoneOf(i) == zone.Id).OrderBy(GroupRank).ThenBy(x => x).ToList();
            int n = 0;
            foreach (var b in bindings)
            {
                n++;
                // Each mapping is one full-width, clearly labelled card so the
                // whole list reads top to bottom without cramped side-by-side
                // boxes. Fields fill the panel width and are never clipped.
                var card = new StackPanel { Spacing = 8 };
                card.Children.Add(new TextBlock
                { Text = $"Mapping {n}", FontSize = Size("SmallSize"), FontWeight = FontWeight.SemiBold, Classes = { "muted" } });

                // ---- What you do (one row per input, each removable) ----
                card.Children.Add(FieldLabel("You do this"));
                int inputCount = Math.Max(1, b.Inputs.Count);
                for (int i = 0; i < inputCount && i < 8; i++)
                {
                    var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var inputBox = TokenField(b.Row, 2 + i, i < b.Inputs.Count ? b.Inputs[i] : "",
                        i == 0 && zoneInputs.Count > 0 ? zoneInputs : InputSuggestions,
                        t => InputLabel(t, zone.Id),
                        $"Input {i + 1} for this {zone.Display} mapping", InputTint);
                    Grid.SetColumn(inputBox, 0);
                    inputRow.Children.Add(inputBox);
                    // Remove a specific input (not just empty it) when there is
                    // more than one committed input on the row.
                    if (b.Inputs.Count > 1 && i < b.Inputs.Count)
                    {
                        int idx = i;
                        var rmv = IconButton("IconDelete", $"Remove this input from mapping {n}");
                        rmv.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                        rmv.Click += (_, _) =>
                        {
                            _file!.RemoveInput(b.Row, idx);
                            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                            FocusZoneDetailSibling(zone.Id, bindings!.IndexOf(b));
                        };
                        Grid.SetColumn(rmv, 1);
                        inputRow.Children.Add(rmv);
                    }
                    card.Children.Add(inputRow);
                }
                if (inputCount < 8)
                {
                    var addInput = new Button
                    { Content = "+ Add another input", Classes = { "quiet" }, HorizontalContentAlignment = HorizontalAlignment.Left };
                    AutomationProperties.SetName(addInput, $"Add another input to mapping {n}; both inputs must be active together");
                    int nextCol = 2 + inputCount;
                    addInput.Click += (_, _) =>
                    {
                        var newRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                        var newBox = TokenField(b.Row, nextCol, "", InputSuggestions,
                            t => InputLabel(t, zone.Id), $"Extra input for mapping {n}", InputTint);
                        Grid.SetColumn(newBox, 0);
                        newRow.Children.Add(newBox);
                        card.Children.Insert(card.Children.IndexOf(addInput), newRow);
                        nextCol++;
                        if (nextCol >= 2 + 8) card.Children.Remove(addInput);
                        (newBox as Border)?.Child?.Focus();
                    };
                    card.Children.Add(addInput);
                }

                // ---- It presses (game button) ----
                card.Children.Add(FieldLabel("It presses this game button"));
                card.Children.Add(TokenField(b.Row, 0, b.Output, OutputSuggestionsFor(CurrentSheet!),
                    TokenLabel, $"Game button pressed by {ShortInput(zone, b)}", OutputTint));

                // ---- How it presses (a self-describing dropdown) ----
                card.Children.Add(FieldLabel("How it presses"));
                card.Children.Add(FunctionCombo(b, zone));

                // ---- Remove this whole mapping (real button, never a bare "X") ----
                var del = new Button { Classes = { "danger" }, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
                del.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { Glyph("IconDelete", "Error"), new TextBlock { Text = "Remove this mapping", FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center } },
                };
                AutomationProperties.SetName(del, $"Remove the {ShortInput(zone, b)} mapping");
                del.Click += (_, _) =>
                {
                    int deletedIndex = bindings!.IndexOf(b);
                    _file!.DeleteRow(b.Row);
                    BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                    FocusZoneDetailSibling(zone.Id, deletedIndex);
                };
                card.Children.Add(del);

                var mappingCard = new Border
                {
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(10),
                    Padding = new Avalonia.Thickness(14),
                    Child = card,
                };
                BindBrush(mappingCard, Border.BackgroundProperty, "Surface");
                BindBrush(mappingCard, Border.BorderBrushProperty, "SurfaceBorder");
                ZoneDetailPanel.Children.Add(mappingCard);
            }
        }
        else
            ZoneDetailPanel.Children.Add(new TextBlock
            { Text = "Nothing mapped here yet.", FontSize = Size("BodySize"), Classes = { "muted" } });

        if (zone.Id != "unset")
        {
            var add = new Button { Content = "+ Map something to this", Classes = { "quiet" } };
            AutomationProperties.SetName(add, $"Add a new mapping for the {zone.Title}");
            add.Click += (_, _) =>
            {
                if (_file is null || CurrentSheet is null) return;
                int newRow = _file.AddBindingRow(CurrentSheet);
                _file.SetCell(newRow, 2, zone.DefaultInput);
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                // Take the user to the mapping they just created (mirrors AddRow in List View).
                if (_cellBorders.TryGetValue($"C{newRow}", out var newBorder))
                { newBorder.BringIntoView(); (newBorder.Child as AutoCompleteBox)?.Focus(); }
            };
            ZoneDetailPanel.Children.Add(add);
        }
    }

    public void LoadProfile(ProfileFile file) => OpenInEditor(file, savePath: null);

    public void SelectZoneForPreview(string zoneId)
    { _selectedZone = zoneId; BuildDeviceView(); BuildZoneDetail(); }

    public void SetModelForPreview(int index)
    { ModelPicker.SelectedIndex = index; }

    async Task OpenAsync()
    {
        var picks = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open QuadStick profile",
            FileTypeFilter = new[] { new FilePickerFileType("QuadStick profile CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (picks.Count == 0) return;
        OpenInEditor(ProfileFile.Load(await File.ReadAllTextAsync(picks[0].Path.LocalPath)), picks[0].Path.LocalPath);
    }

    async Task SaveAsync()
    {
        if (_file is null) return;
        if (_savePath is null)
        {
            Directory.CreateDirectory(LibraryDir);
            var start = await StorageProvider.TryGetFolderFromPathAsync(LibraryDir);
            var pick = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save profile CSV",
                SuggestedFileName = _file.Document.CsvFileName ?? "profile.csv",
                SuggestedStartLocation = start,
                DefaultExtension = "csv",
            });
            if (pick is null) return;
            _savePath = pick.Path.LocalPath;
        }
        _file.EnsureVersionHeader(); // saved files match installed files byte for byte
        await File.WriteAllTextAsync(_savePath, _file.ToCsvText());
        _file.Dirty = false;
        RebuildRows(); // header insertion shifts grid rows
        Status($"Saved to {_savePath}.", StatusKind.Ready);
    }

    async Task ImportAsync()
    {
        void HomeError(string message)
        {
            HomeStatusText.Text = message;
            HomeStatusText.IsVisible = true;
            SheetsUrlBox.Focus();
        }
        HomeStatusText.IsVisible = false;

        var pasted = SheetsUrlBox.Text ?? "";
        if (!SheetsUrl.TryGetCsvExportUrl(pasted, out var url))
        { HomeError("That does not look like a Google Sheets link. Paste the full link from your browser's address bar."); return; }
        try
        {
            var text = await Http.GetStringAsync(url);
            if (text.TrimStart().StartsWith('<'))
            { HomeError("Google returned a web page instead of the profile. The sheet is probably not shared publicly (File > Share > Anyone with the link)."); return; }
            var imported = ProfileFile.Load(text);
            OpenInEditor(imported, savePath: null);
            if (imported.Document.Sheets.Count == 1)
                Status("Imported this spreadsheet's linked tab. If the profile has more mode tabs, they are not included yet; importing every tab is coming.", StatusKind.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { HomeError($"Could not download the sheet: {(ex is TaskCanceledException ? "the connection timed out after 15 seconds" : ex.Message)}. Check your internet connection and the link."); }
    }

    bool _closeConfirmed;

    void CommitFileName()
    {
        if (_file is null) return;
        var v = (FileNameBox.Text ?? "").Trim();
        if (v.Length == 0 || v == _file.Document.CsvFileName) return;
        _file.SetCell(_file.Document.FileNameCellRow, 0, v);
        Title = $"QuadStick Config Manager (unofficial) - {v}";
        RefreshIssues(); // bad names surface immediately as errors
    }

    async Task<bool> ConfirmLeaveAsync()
    {
        if (_file is not { Dirty: true }) return true;
        return await ConfirmAsync("Leave without saving?",
            "This profile has unsaved changes. If you leave now they are lost. Leave anyway?");
    }

    void UndoEdit()
    {
        if (_file is null || !_file.Undo()) { Status("Nothing to undo."); return; }
        FileNameBox.Text = _file.Document.CsvFileName ?? "";
        RefreshEditor();
        Status("Change undone.", StatusKind.Ready);
    }

    ModeSheet? CurrentSheet =>
        _file != null && _sheetIndex < _file.Document.Sheets.Count ? _file.Document.Sheets[_sheetIndex] : null;

    void RebuildRows()
    {
        RowsPanel.Children.Clear();
        _cellBorders.Clear();
        if (CurrentSheet is null) { RefreshIssues(); return; }

        bool prefs = CurrentSheet.Type != SheetType.ProfileName;
        RowsPanel.Children.Add(prefs ? PrefsHeaderRow() : HeaderRow());
        foreach (var b in CurrentSheet.Bindings)
            RowsPanel.Children.Add(prefs ? PrefsRow(b) : BindingRow(b));

        if (CurrentSheet.Bindings.Count == 0)
            RowsPanel.Children.Add(new TextBlock
            {
                Text = prefs
                    ? "No settings on this sheet yet. Click \"Add row\" to add one."
                    : "No bindings yet. Click \"Add row\" to connect an input to an output.",
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Avalonia.Thickness(4, 12),
            });
        RefreshIssues();
    }

    Control HeaderRow()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(Swatch("Output (game button)", 220, OutputTint));
        p.Children.Add(Swatch("Function (behavior)", 180, FunctionTint));
        p.Children.Add(Swatch("Inputs (sips, puffs, joystick)", 240, InputTint));
        return p;

        static Control Swatch(string text, double width, string tintKey)
        {
            var border = new Border
            {
                Width = width, CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(8, 4),
                Child = new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = Size("SmallSize") },
            };
            BindBrush(border, Border.BackgroundProperty, tintKey);
            return border;
        }
    }

    Control PrefsHeaderRow()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var settingHeader = new Border
        {
            Width = 300, CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, 4),
            Child = new TextBlock { Text = "Setting", FontWeight = FontWeight.Bold, FontSize = Size("SmallSize") },
        };
        BindBrush(settingHeader, Border.BackgroundProperty, OutputTint);
        p.Children.Add(settingHeader);
        var valueHeader = new Border
        {
            Width = 160, CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, 4),
            Child = new TextBlock { Text = "Value", FontWeight = FontWeight.Bold, FontSize = Size("SmallSize") },
        };
        BindBrush(valueHeader, Border.BackgroundProperty, FunctionTint);
        p.Children.Add(valueHeader);
        return p;
    }

    Control PrefsRow(Binding b)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(SuggestBox(b.Row, 0, b.Output, 300, NoSuggestions, $"Setting name for row {b.Row}", OutputTint));
        p.Children.Add(SuggestBox(b.Row, 1, b.Function, 160, NoSuggestions, $"Setting value for row {b.Row}", FunctionTint));
        var del = new Button { Content = "Delete row", Classes = { "danger" } };
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => DeleteListRow(b);
        p.Children.Add(del);
        return p;
    }

    Control BindingRow(Binding b)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(SuggestBox(b.Row, 0, b.Output, 220, OutputSuggestionsFor(CurrentSheet!), $"Output for row {b.Row}", OutputTint));
        p.Children.Add(SuggestBox(b.Row, 1, b.Function, 180, FunctionSuggestions, $"Function for row {b.Row}", FunctionTint));

        int inputCount = Math.Max(1, b.Inputs.Count);
        for (int i = 0; i < inputCount; i++)
        {
            p.Children.Add(SuggestBox(b.Row, 2 + i, i < b.Inputs.Count ? b.Inputs[i] : "", 240,
                InputSuggestions, $"Input {i + 1} for row {b.Row}", InputTint));
            // A round remove control right after each real input, so any input
            // can be taken out (not just emptied, and not just the last one).
            if (b.Inputs.Count > 1 && i < b.Inputs.Count)
            {
                int idx = i;
                var rmv = IconButton("IconDelete", $"Remove input {i + 1} from row {b.Row}");
                rmv.Click += (_, _) =>
                {
                    var off = GridScroll.Offset;
                    _file!.RemoveInput(b.Row, idx);
                    RebuildRows();
                    RestoreListScroll(off, () =>
                    {
                        if (_cellBorders.TryGetValue($"A{b.Row}", out var border))
                        { border.BringIntoView(); (border.Child as AutoCompleteBox)?.Focus(); }
                    });
                };
                p.Children.Add(rmv);
            }
        }

        if (inputCount < 8)
        {
            var addInput = new Button { Content = "+ input", Classes = { "quiet" } };
            AutomationProperties.SetName(addInput, $"Add another input to row {b.Row}");
            int nextCol = 2 + inputCount;
            addInput.Click += (_, _) =>
            {
                // Add the box directly; the file only changes when a value is committed.
                var newBox = SuggestBox(b.Row, nextCol, "", 240, InputSuggestions,
                    $"Input {nextCol - 1} for row {b.Row}", InputTint);
                p.Children.Insert(p.Children.IndexOf(addInput), newBox);
                nextCol++;
                if (nextCol >= 2 + 8) p.Children.Remove(addInput);
                ((newBox as Border)!.Child as AutoCompleteBox)!.Focus();
            };
            p.Children.Add(addInput);
        }

        var del = new Button { Content = "Delete row", Classes = { "danger" } };
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => DeleteListRow(b);
        p.Children.Add(del);
        return p;
    }

    // Delete a List View row without the scroll jumping to the top: restore the
    // saved scroll offset after the rebuild, then focus the row that slid into
    // place. (RebuildRows() clears and re-adds every row, which otherwise resets
    // the ScrollViewer to 0.)
    void DeleteListRow(Binding b)
    {
        int deletedIndex = CurrentSheet!.Bindings.IndexOf(b);
        var off = GridScroll.Offset;
        _file!.DeleteRow(b.Row);
        RebuildRows();
        RestoreListScroll(off, () => FocusRowSibling(deletedIndex));
    }

    void RestoreListScroll(Vector offset, Action thenFocus) =>
        Dispatcher.UIThread.Post(() =>
        {
            var maxY = Math.Max(0, GridScroll.Extent.Height - GridScroll.Viewport.Height);
            GridScroll.Offset = new Vector(offset.X, Math.Min(offset.Y, maxY));
            thenFocus();
        }, DispatcherPriority.Loaded);

    Control SuggestBox(int row, int col, string value, double width, List<string> suggestions, string accessibleName, string tintKey, bool stretch = false)
    {
        var box = new AutoCompleteBox
        {
            Text = value,
            ItemsSource = suggestions,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
        };
        if (stretch) box.HorizontalAlignment = HorizontalAlignment.Stretch;
        else box.Width = width;
        box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
        AutomationProperties.SetName(box, accessibleName);
        // Open the list of choices as soon as the field gets focus, so you can
        // click and pick instead of having to know and type a value.
        if (suggestions.Count > 0)
            box.GotFocus += (_, _) => box.IsDropDownOpen = true;
        void Commit()
        {
            if (_file is null) return;
            var v = (box.Text ?? "").Trim();
            if (v == _file.GetCell(row, col)) return; // no no-op undo snapshots
            _file.SetCell(row, col, v);
            RefreshIssues();
            // Keep the device diagram's summaries current without stealing
            // focus from the detail panel the user is typing in.
            if (DeviceContainer.IsVisible) BuildDeviceView();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var wrapper = new Border
        {
            Child = box,
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;
        return wrapper;
    }

    void AddRow()
    {
        if (_file is null || CurrentSheet is null) { Status("Open or create a profile first."); return; }
        int newRow = _file.AddBindingRow(CurrentSheet);
        _file.Reparse();
        if (DeviceContainer.IsVisible)
        {
            _selectedZone = "unset"; // the new row has no input yet; take the user to it
            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            return;
        }
        RebuildRows();
        // Take the user to the row they just created.
        if (_cellBorders.TryGetValue($"A{newRow}", out var border))
        {
            border.BringIntoView();
            (border.Child as AutoCompleteBox)?.Focus();
        }
    }

    // After deleting a List View row, keep focus on the row that slid into
    // its place instead of dropping it (mirrors AddRow's "take the user
    // there" logic in reverse).
    void FocusRowSibling(int deletedIndex)
    {
        if (CurrentSheet is { Bindings.Count: > 0 } sheet)
        {
            int idx = Math.Min(deletedIndex, sheet.Bindings.Count - 1);
            var targetRow = sheet.Bindings[idx].Row;
            if (_cellBorders.TryGetValue($"A{targetRow}", out var border))
            { border.BringIntoView(); (border.Child as AutoCompleteBox)?.Focus(); return; }
        }
        AddRowButton.Focus(); // no rows left; hand focus to the control that adds one
    }

    // Same idea for Device View's zone detail panel: focus the mapping card
    // that took the deleted one's place, or the "add mapping" button, or a
    // zone button, so a keyboard/switch user is never left with no focus.
    void FocusZoneDetailSibling(string zoneId, int deletedIndex)
    {
        BindingsByZone().TryGetValue(zoneId, out var remaining);
        int count = remaining?.Count ?? 0;
        if (count > 0)
        {
            int childIndex = 2 + Math.Min(deletedIndex, count - 1); // 0: title, 1: blurb, 2+: cards
            if (childIndex < ZoneDetailPanel.Children.Count
                && ZoneDetailPanel.Children[childIndex] is Control card
                && FindFocusable(card) is { } target)
            { target.BringIntoView(); target.Focus(); return; }
        }
        if (ZoneDetailPanel.Children.OfType<Button>().FirstOrDefault() is { } addButton)
        { addButton.Focus(); return; }
        if (_zoneButtons.TryGetValue(zoneId, out var zoneButton)) zoneButton.Focus();
        else if (_zoneButtons.Count > 0) _zoneButtons.Values.First().Focus();
    }

    // Depth-first search for the first AutoCompleteBox or Button under a
    // mapping card's Border/StackPanel wrapper tree.
    static Control? FindFocusable(Control root) => root switch
    {
        AutoCompleteBox or Button or ComboBox => root,
        Border { Child: Control c } => FindFocusable(c),
        Panel p => p.Children.Select(FindFocusable).FirstOrDefault(f => f != null),
        _ => null,
    };

    // Jumps focus to the field an issue is about: the file name box for
    // filename problems, or the matching grid cell otherwise. Switches mode
    // sheet and, in Device View, zone selection first if the cell lives
    // somewhere not currently on screen.
    void FocusIssueCell(Issue issue)
    {
        if (_file is null) return;
        if (issue.Cell == $"A{_file.Document.FileNameCellRow}")
        { FileNameBox.Focus(); return; }

        if (int.TryParse(issue.Cell.AsSpan(1), out int row))
        {
            int sheetIdx = _file.Document.Sheets.FindIndex(s => s.Bindings.Any(b => b.Row == row));
            if (sheetIdx >= 0 && sheetIdx != _sheetIndex)
                SheetPicker.SelectedIndex = sheetIdx; // triggers RefreshEditor synchronously

            if (_deviceView && sheetIdx >= 0)
            {
                var binding = _file.Document.Sheets[sheetIdx].Bindings.First(b => b.Row == row);
                var zoneId = binding.Inputs.Count > 0 ? ZoneOf(binding.Inputs[0]) : "unset";
                if (_selectedZone != zoneId)
                {
                    _selectedZone = zoneId;
                    BuildDeviceView(); BuildZoneDetail();
                }
            }
        }

        if (_cellBorders.TryGetValue(issue.Cell, out var border))
        {
            border.BringIntoView();
            (border.Child as AutoCompleteBox)?.Focus();
        }
    }

    void RefreshIssues()
    {
        foreach (var b in _cellBorders.Values)
        {
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Avalonia.Thickness(2);
            if (b.Child is Control c) AutomationProperties.SetName(b, AutomationProperties.GetName(c));
        }
        if (_file is null) { IssuesList.ItemsSource = null; return; }

        IssuesList.ItemsSource = _file.Issues.Count == 0
            ? new List<Control>
              {
                  new TextBlock { Text = "No problems found.", FontSize = Size("SmallSize"),
                                  Classes = { "success" }, Margin = new Avalonia.Thickness(4) },
              }
            : _file.Issues
                .OrderBy(i => i.Severity == Severity.Error ? 0 : 1)
                .Select(i => (Control)new TextBlock
                {
                    Text = i.ToString(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Size("SmallSize"),
                    Classes = { i.Severity == Severity.Error ? "error" : "warn" },
                    Tag = i, // lets SelectionChanged/Fix-first find the cell to focus
                })
                .ToList();

        foreach (var issue in _file.Issues)
            if (_cellBorders.TryGetValue(issue.Cell, out var border))
            {
                var severityLabel = issue.Severity == Severity.Error ? "Error" : "Warning";
                BindBrush(border, Border.BorderBrushProperty, severityLabel);
                border.BorderThickness = new Avalonia.Thickness(3);
                var baseName = border.Child is Control c ? AutomationProperties.GetName(c) : null;
                AutomationProperties.SetName(border, $"{severityLabel}: {baseName}");
            }

        var errors = _file.Issues.Count(i => i.Severity == Severity.Error);
        var warns = _file.Issues.Count - errors;
        Status(errors + warns == 0
                ? "No problems. Ready to save or install."
                : $"{errors} error(s), {warns} warning(s). Errors block installing.",
            errors > 0 ? StatusKind.Error : warns > 0 ? StatusKind.Warning : StatusKind.Ready);
        UpdateProblemsToggle();
    }

    // ---- Small shared UI builders for the redesigned editor ----

    static TextBlock FieldLabel(string text) => new()
    { Text = text, FontSize = Size("SmallSize"), FontWeight = FontWeight.SemiBold, Classes = { "muted" } };

    static PathIcon Glyph(string iconKey, string tokenKey)
    {
        var icon = new PathIcon { Width = 16, Height = 16, Data = (Geometry)Application.Current!.FindResource(iconKey)! };
        BindBrush(icon, IconElement.ForegroundProperty, tokenKey);
        return icon;
    }

    static Button IconButton(string iconKey, string accessibleName)
    {
        var b = new Button { Classes = { "icon" }, Content = Glyph(iconKey, "TextSecondary") };
        AutomationProperties.SetName(b, accessibleName);
        return b;
    }

    // Function picker: a real dropdown (opens on click, no typing needed) whose
    // items spell out in plain words what each behavior does, so the user never
    // has to already know what "toggle" or "pulse" means. Exotic values with
    // parameters (e.g. "repeat 5 2000") stay selectable so nothing is lost.
    Control FunctionCombo(Binding b, Zone zone)
    {
        var items = new List<string>(FunctionSuggestions);
        var current = (b.Function ?? "").Trim();
        if (current.Length > 0 && !items.Contains(current)) items.Insert(0, current);

        var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectedItem = items.FirstOrDefault(x => x == current);
        combo.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var sp = new StackPanel { Spacing = 1, Margin = new Avalonia.Thickness(0, 2) };
            sp.Children.Add(new TextBlock { Text = TokenLabel(name), FontWeight = FontWeight.SemiBold, FontSize = Size("BodySize") });
            var d = FunctionExplain(name);
            if (d.Length > 0)
                sp.Children.Add(new TextBlock { Text = d, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            return sp;
        });
        AutomationProperties.SetName(combo, $"How {ShortInput(zone, b)} presses it. {FunctionExplain(current)}");
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string s && _file != null && s != _file.GetCell(b.Row, 1))
            {
                _file.SetCell(b.Row, 1, s);
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            }
        };
        return combo;
    }

    // The item that reveals a free-text box at the very bottom of a Device View
    // dropdown, so an exotic value is still reachable without making typing the
    // default. Reference-compared, never shown as a real token.
    const string TypeYourOwn = "＋ Type your own…";

    // A pick-don't-type field for Device View: a dropdown of known tokens shown
    // in the current label style, committing the raw token to the cell. The
    // last entry drops to a text box for anything not on the list. Keeps the
    // wrapper registered in _cellBorders so problem highlighting still lands.
    Control TokenField(int row, int col, string current, IReadOnlyList<string> options,
                       Func<string, string> labelFor, string accessibleName, string tintKey)
    {
        var wrapper = new Border
        {
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;

        void ShowCombo()
        {
            var items = new List<string>(options);
            var cur = (current ?? "").Trim();
            if (cur.Length > 0 && !items.Contains(cur)) items.Insert(0, cur);
            items.Add(TypeYourOwn);

            var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectedItem = cur.Length > 0 ? items.FirstOrDefault(x => x == cur) : null;
            combo[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
            combo.ItemTemplate = new FuncDataTemplate<string>((token, _) =>
            {
                bool own = ReferenceEquals(token, TypeYourOwn);
                var tb = new TextBlock
                {
                    Text = own ? TypeYourOwn : labelFor(token),
                    FontSize = Size("BodySize"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 2),
                };
                if (own) tb.Classes.Add("muted");
                return tb;
            });
            AutomationProperties.SetName(combo, accessibleName);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not string s || _file is null) return;
                if (ReferenceEquals(s, TypeYourOwn)) { ShowTyping(); return; }
                if (s == _file.GetCell(row, col)) return;
                _file.SetCell(row, col, s);
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            };
            wrapper.Child = combo;
        }

        void ShowTyping()
        {
            var box = new AutoCompleteBox
            {
                Text = "", ItemsSource = options, FilterMode = AutoCompleteFilterMode.Contains,
                MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                Watermark = "type a value, or leave blank to go back",
            };
            box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
            AutomationProperties.SetName(box, accessibleName + ". Type a custom value.");
            void Commit()
            {
                if (_file is null) return;
                var v = (box.Text ?? "").Trim();
                if (v.Length == 0) { ShowCombo(); return; } // empty = cancel, back to the list
                if (v != _file.GetCell(row, col)) { _file.SetCell(row, col, v); }
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            wrapper.Child = box;
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }

        ShowCombo();
        return wrapper;
    }

    // A dismissable popup anchored to its "?" button: the answer is one click
    // away and never clutters the editing surface.
    static void ShowInfoFlyout(Control anchor, string title, string body)
    {
        var content = new StackPanel { Spacing = 8, MaxWidth = 340, Margin = new Avalonia.Thickness(4) };
        content.Children.Add(new TextBlock
        { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        new Flyout { Content = content, Placement = PlacementMode.Bottom }.ShowAt(anchor);
    }

    bool _problemsExpanded;

    void ToggleProblems()
    {
        _problemsExpanded = !_problemsExpanded;
        ProblemsListBorder.IsVisible = _problemsExpanded;
        UpdateProblemsToggle();
    }

    // The bottom problems bar: always a slim one-line summary; the full list
    // expands above it only when asked. Icon + count read at a glance.
    void UpdateProblemsToggle()
    {
        int errors = 0, warns = 0;
        if (_file != null)
        {
            errors = _file.Issues.Count(i => i.Severity == Severity.Error);
            warns = _file.Issues.Count - errors;
        }
        string iconKey, token, label;
        if (errors + warns == 0)
        {
            iconKey = "IconCheck"; token = "Success";
            label = _problemsExpanded ? "No problems (click to hide)" : "No problems";
        }
        else
        {
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} error{(errors == 1 ? "" : "s")}");
            if (warns > 0) parts.Add($"{warns} warning{(warns == 1 ? "" : "s")}");
            iconKey = errors > 0 ? "IconError" : "IconWarning";
            token = errors > 0 ? "Error" : "Warning";
            label = string.Join(", ", parts) + (_problemsExpanded ? "  (click to hide)" : "  (click to view)");
        }
        var text = new TextBlock { Text = label, FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center };
        BindBrush(text, TextBlock.ForegroundProperty, token);
        ProblemsToggle.Content = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Glyph(iconKey, token), text } };
        FixFirstButton.IsVisible = _problemsExpanded && errors > 0;
    }

    // Plain words for what a Function does, keyed on its first token so
    // "repeat 5 2000" still explains. Empty when blank or unknown, so the
    // note only appears once a behavior is actually chosen.
    static string FunctionExplain(string function)
    {
        var name = (function ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return name switch
        {
            "normal" => "Pressed while your input is active.",
            "toggle" => "One activation latches it on, the next releases it.",
            "repeat" => "Rapid-fire taps while your input is held.",
            "pulse" => "One short press each time you activate.",
            "delayed_latch" => "A short activation taps it; a long one latches it on.",
            "delay_on" => "Waits a moment after you activate, then presses.",
            "delay_off" => "Keeps pressing for a moment after you release.",
            "tap" => "Splits one input into two outputs by how long you hold.",
            "force_off" => "Releases another latched output.",
            "greater_than" => "Fires once your input passes a set strength.",
            "less_than" => "Fires while your input stays under a set strength.",
            "duty" => "Presses in a repeating on and off cycle.",
            "increment_value" => "Nudges a device setting up, like mouse speed.",
            "decrement_value" => "Nudges a device setting down, like mouse speed.",
            _ => "",
        };
    }

    void Status(string text, StatusKind kind = StatusKind.Info)
    {
        StatusHost.Content = StatusChip(kind, text);
        AutomationProperties.SetLiveSetting(StatusHost,
            kind == StatusKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    // Shared by ShowHelp() and the Settings window's Help tab (DRY): one
    // ground truth for the quick-guide copy.
    internal static (string Title, string Body)[] HelpSections() => new (string Title, string Body)[]
        {
            ("What is a profile?",
             "One CSV file that tells the QuadStick which sip, puff, lip press, or joystick move presses which game button. A profile has one or more mode sheets: full control layouts you switch between while playing (walking layout, driving layout, menus). Sip or puff the side tube, or bind increment_mode / decrement_mode, to switch modes in-game."),

            ("The three columns (same colors as the official spreadsheets)",
             "Yellow, OUTPUT: the game button or action. PlayStation names (x, circle, left_1), Xbox names (A, B, left_trigger), mouse (mouse_up, mouse_left_button), keyboard (kb_space), and even device settings like mouse_speed, which increment_value can adjust mid-game.\n" +
             "Pink, FUNCTION: how the press behaves.\n" +
             "  normal: pressed while your input is active.\n" +
             "  toggle: one activation latches it on, the next releases it. Great for aiming without holding a sip.\n" +
             "  repeat [rate] [delay]: rapid-fire taps while held. \"repeat 5 2000\" holds 2 seconds, then taps 5x per second.\n" +
             "  pulse [ms] [count]: one short press per activation. \"pulse 50 2\" double-taps.\n" +
             "  delayed_latch [ms]: short activation = normal press, long activation = latch. Two behaviors from one input.\n" +
             "  tap / delay_on: split one input into two outputs by press length.\n" +
             "  force_off: releases another latched output. greater_than / less_than: threshold triggers. duty, increment_value, decrement_value: analog control.\n" +
             "Blue, INPUTS: what your mouth does. mp_… names are mouthpiece holes (mp_left_sip). …_soft variants trigger on gentle pressure. right_sip / right_puff are the side tube. lip is the lip switch. left/right/up/down and N/NE/… are the joystick. Several inputs on one row must all be active together."),

            ("Start from a working profile, not from scratch",
             "New profile gives you the factory default layout, the same one shipped on every QuadStick. The community also shares hundreds of game profiles as Google Sheets: paste any share link on the home screen to import it. Then adjust, rename, save."),

            ("Renaming",
             "The file name box at the top of the editor is the profile's on-device name. It must end in .csv, with no spaces. default.csv is special: it is the device's fallback file and should stay unchanged."),

            ("Installing safely",
             "Plug in the QuadStick; it shows up like a USB drive. Install backs up the old file to QuadStickBackups, writes a temp copy, checks it, then swaps it in. Errors block install. Overwriting default.csv always asks first."),

            ("QuadStick not showing up?",
             "If the device is in PS4 boot mode, or virtual XBox / Dualshock controller emulation is enabled, the flash drive does not appear on the computer. Turn those off (in QMP or your prefs) and replug. On a Mac, the volume is named \"QUAD STICK\" in Finder."),

            ("Keyboard",
             "Tab moves between fields, arrows navigate suggestion lists, Enter confirms. Ctrl/Cmd+O open, S save, N new, Z undo, I install, D switch between Device view and List view, H this guide. F1 also opens this guide from anywhere. Selecting a problem, or the Fix first problem button, jumps focus straight to it. Every control announces itself to screen readers."),

            ("Found a problem?",
             "Select a problem in the list to copy it. File at github.com/Bbrizly/Quadstick-Config-Manager/issues. Say what you did and what went wrong."),
        };

    void ShowHelp()
    {
        var sections = HelpSections();

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14, MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = "Quick guide", FontSize = Size("TitleSize"), FontWeight = FontWeight.Bold });
        foreach (var (title, body) in sections)
        {
            panel.Children.Add(new TextBlock
            { Text = title, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
            panel.Children.Add(new TextBlock
            { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        var win = new Window
        {
            Title = "Quick guide",
            Width = Math.Min(720 * _uiScale, 1200), Height = Math.Min(680 * _uiScale, 900),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = ZoomWrap(panel, _uiScale) },
        };
        win.Show(this);
    }

    async Task<string?> PickDeviceRootAsync(IReadOnlyList<string> candidates)
    {
        string? picked = null;
        var cancel = new Button { Content = "Cancel", MinWidth = 140, IsCancel = true };
        var choices = new StackPanel { Spacing = 8 };
        var dialog = new Window
        {
            Title = "Choose QuadStick",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        foreach (var root in candidates)
        {
            var label = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(label);
            if (string.IsNullOrEmpty(name)) name = label;
            var btn = new Button
            {
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = name, FontWeight = FontWeight.Bold, FontSize = Size("BodySize") },
                        new TextBlock { Text = root, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    },
                },
                MinWidth = 360,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = root,
            };
            btn.Click += (_, _) => { picked = (string)btn.Tag!; dialog.Close(); };
            choices.Children.Add(btn);
        }
        dialog.Content = ZoomWrap(new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = "Multiple QuadSticks found", FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize") },
                new TextBlock { Text = "Choose which drive to install to:", TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                choices,
                cancel,
            },
        }, _uiScale);
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return picked;
    }

    async Task<bool> ConfirmAsync(string title, string message)
    {
        var yes = new Button { Content = "Yes, continue", MinWidth = 140 };
        var no = new Button { Content = "Cancel", MinWidth = 140, IsDefault = true, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } },
                },
            }, _uiScale),
        };
        var result = false;
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }
}
