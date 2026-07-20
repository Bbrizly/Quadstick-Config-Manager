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
        else icon = Glyph(iconKey, tokenKey);
        var label = new TextBlock { Text = text, FontSize = Size("BodySize"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        BindBrush(label, TextBlock.ForegroundProperty, tokenKey);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { icon, label } };
    }

    ProfileFile? _file;
    string? _savePath;          // where Save writes; null until saved or opened from a path
    int _sheetIndex;
    bool _deviceView = true;    // true = the split editor (diagram OR rail); false = the raw List View
    bool _railView;             // when in the split editor, show the parts as a list instead of the diagram
    string? _selectedZone;
    // Device View shows friendly words ("soft sip") by default; the Words
    // button cycles plain English -> Xbox-style button names -> the raw token
    // the List View and the CSV use ("mp_left_sip_soft"), so the views speak
    // whichever vocabulary the user thinks in.
    int _labelStyle = 1; // 0 = raw list names, 1 = plain English, 2 = Xbox style
    bool _friendlyLabels => _labelStyle != 0;
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

    // Templates are just saved profile CSVs kept in a subfolder of the library.
    // The library card list reads LibraryDir non-recursively, so this subfolder
    // never leaks into "Your profiles". A template opens as a fresh local copy
    // (savePath null), so editing and installing never touches the template.
    public static string TemplatesDir => Path.Combine(LibraryDir, "Templates");

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

        HomeNewButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) NewFromTemplate(); };
        HomeTemplateButton.Click += async (_, _) => await UseTemplateAsync();
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
        SaveTemplateButton.Click += async (_, _) => await SaveAsTemplateAsync();
        InstallButton.Click += async (_, _) => await RunInstallFlowAsync();
        HelpButton.Click += (_, _) => ShowHelp();
        AddRowButton.Click += (_, _) => AddRow();
        AddModeButton.Click += async (_, _) => await AddModeAsync();
        ModeMenuButton.Click += (_, _) => ShowModeMenu();
        // A click that lands on nothing selectable drops the row selection,
        // exactly like a file explorer. Row-number presses mark themselves
        // Handled, so they never reach this.
        GridScroll.AddHandler(PointerPressedEvent, (_, _) => ClearSelection());
        SelectionDeleteButton.Click += (_, _) => DeleteSelectedRows();
        SelectionClearButton.Click += (_, _) => ClearSelection();
        SelectionMoveButton.Flyout = MoveMenu();
        DeviceSelectionDeleteButton.Click += (_, _) => DeleteSelectedRows();
        DeviceSelectionClearButton.Click += (_, _) => ClearSelection();
        DeviceSelectionMoveButton.Flyout = MoveMenu();

        // Device view mappings read as plain sentences by default; this flips
        // to the detailed editor for users who want every field on screen.
        CardViewButton.Click += (_, _) =>
        {
            _settings.DeviceCards = !_settings.DeviceCards;
            Settings.Save(_settings);
            UpdateCardViewButton();
            BuildZoneDetail();
        };
        UpdateCardViewButton();

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
            // An item is a bare TextBlock, or a StackPanel wrapping the text
            // plus a quick-fix button (unknown-input errors).
            var tb = IssuesList.SelectedItem as TextBlock
                ?? (IssuesList.SelectedItem as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (tb is { Text.Length: > 0 } && Clipboard is { } cb)
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

        DeviceViewButton.Click += (_, _) => SetDeviceView(true, rail: false);
        RailViewButton.Click += (_, _) => SetDeviceView(true, rail: true);
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
        EditorSettingsButton.Click += (_, _) => new SettingsWindow(this).ShowDialog(this);

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
                else if (e.Key == Key.Escape && _selectedRows.Count > 0)
                { ClearSelection(); e.Handled = true; }
                else if (e.Key == Key.Escape && _expandedMapping >= 0 && DeviceContainer.IsVisible)
                { _expandedMapping = -1; BuildZoneDetail(); e.Handled = true; }
                else if (e.Key == Key.Delete && _selectedRows.Count > 0
                         && e.Source is not (TextBox or AutoCompleteBox))
                { DeleteSelectedRows(); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                // Open and New both discard the current profile, so they get
                // the same unsaved-changes gate as Home and window close.
                case Key.O: _ = GuardedAsync(OpenAsync); e.Handled = true; break;
                case Key.S: _ = SaveAsync(); e.Handled = true; break;
                case Key.N: _ = GuardedAsync(async () => { if (await ConfirmLeaveAsync()) NewFromTemplate(); }); e.Handled = true; break;
                case Key.Z: UndoEdit(); e.Handled = true; break;
                case Key.I: _ = RunInstallFlowAsync(); e.Handled = true; break;
                case Key.D: SetDeviceView(!_deviceView, _railView); e.Handled = true; break; // keep Parts List sub-mode
                case Key.H: ShowHelp(); e.Handled = true; break;
            }
        };

        _reduceMotion = _settings.ReduceMotion;
        ApplyInterfaceScale(_settings.InterfaceScalePercent);

        // The crash safety net needs to know what to rescue, always.
        CrashGuard.CurrentFile = () => _file;

        // Autosave: every 30 seconds, unsaved work is copied to a draft file.
        // A crash, a dead battery, or a force-quit can then cost at most 30
        // seconds of edits, never an afternoon.
        var autosave = new Avalonia.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(30) };
        autosave.Tick += (_, _) => WriteDraft();
        autosave.Start();

        ShowHome();
        OfferRescueIfAny();
        if (!_settings.TutorialSeen) Opened += StartTutorialOnce;
    }

    // ---------- autosave drafts and crash recovery ----------

    static string DraftPath => Path.Combine(CrashGuard.RescueDir, "autosave-draft.csv");
    int _draftedRevision = -1; // last _file.Revision written to the draft; -1 = none

    void WriteDraft()
    {
        try
        {
            if (_file is { Dirty: true })
            {
                // Nothing edited since the last draft: don't re-serialize the whole
                // grid and rewrite the file every 30s for no reason.
                if (_file.Revision == _draftedRevision) return;
                Directory.CreateDirectory(CrashGuard.RescueDir);
                File.WriteAllText(DraftPath, _file.ToCsvText());
                _draftedRevision = _file.Revision;
            }
            else if (_file is not null && File.Exists(DraftPath))
            {
                // A file is open and clean (just saved): its draft is stale, drop it.
                // When NO file is open we must NOT delete — on startup after a crash
                // that draft is the unopened recovery still offered on the Home screen,
                // and the 30s timer would otherwise erase it out from under the user.
                File.Delete(DraftPath);
                _draftedRevision = -1;
            }
        }
        catch { /* autosave must never interrupt the user */ }
    }

    void OfferRescueIfAny()
    {
        var rescues = CrashGuard.PendingRescues();
        if (rescues.Count == 0) return;
        var newest = rescues[0];
        HomeStatusText.Text =
            $"Unsaved work from last time was recovered ({Path.GetFileName(newest)}). " +
            "Open it from the button below, or dismiss to discard.";
        HomeStatusText.IsVisible = true;
        RescuePanel.IsVisible = true;
        RescueOpenButton.Click += (_, _) =>
        {
            try
            {
                OpenInEditor(ProfileFile.Load(File.ReadAllText(newest)), savePath: null);
                if (_file is not null) _file.Dirty = true; // unsaved recovery: leaving must warn, not silently drop it
                CrashGuard.DiscardRescues(); // now in the editor: the rescue files on disk are spent, don't re-offer them forever
                Status("Recovered profile opened. Save it to keep it.", StatusKind.Warning);
                RescuePanel.IsVisible = false;
                HomeStatusText.IsVisible = false; // the offer is spent; coming back Home must not still announce it
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { HomeStatusText.Text = $"Could not open the recovered file: {ex.Message}"; }
        };
        RescueDismissButton.Click += (_, _) =>
        {
            CrashGuard.DiscardRescues();
            RescuePanel.IsVisible = false;
            HomeStatusText.IsVisible = false;
        };
    }

    internal static readonly int[] ValidScalePercents = { 60, 70, 80, 90, 100, 125, 150, 200 };

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
        _file = null; // Home has no profile open; a leftover dirty file would re-prompt "leave?" on the next action
        HomeView.IsVisible = true;
        EditorView.IsVisible = false;
        Title = "Quadstick: Config Manager (unofficial)"; // no profile is open on Home
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
    void SetDeviceView(bool device, bool rail = false)
    {
        _deviceView = device;
        _railView = device && rail;
        // A selection made in the other view would be invisible here, and an
        // invisible selection must never feed the Delete button.
        _selectedRows.Clear(); _selAnchor = -1;
        RefreshEditor();
        if (device && _railView)
        {
            // Land on the selected row, or the first part, so arrow keys work at once.
            (_zoneButtons.GetValueOrDefault(_selectedZone ?? "") ?? _zoneButtons.Values.FirstOrDefault())?.Focus();
        }
        else if (device)
        {
            if (_zoneButtons.TryGetValue("joystick", out var joystickBtn)) joystickBtn.Focus();
        }
        else if (CurrentSheet?.Bindings.FirstOrDefault()?.Row is int firstRow
                 && _cellBorders.TryGetValue($"A{firstRow}", out var border))
        { border.BringIntoView(); (border.Child as AutoCompleteBox)?.Focus(); }
        else AddRowButton.Focus();
    }

    // Cycle Device View between plain-English words, Xbox-style button names,
    // and the raw list-view/CSV token names, and rebuild so every dropdown
    // label follows suit.
    void ToggleLabelStyle()
    {
        _labelStyle = (_labelStyle + 1) % 3;
        UpdateLabelStyleButton();
        if (_deviceView && CurrentSheet?.Type == SheetType.ProfileName)
        { BuildDeviceView(); BuildZoneDetail(); }
    }

    void UpdateLabelStyleButton()
    {
        LabelStyleButton.Content = _labelStyle switch
        {
            0 => "Words: List names",
            1 => "Words: Plain English",
            _ => "Words: Xbox style",
        };
        AutomationProperties.SetName(LabelStyleButton, _labelStyle switch
        {
            0 => "Words are shown as raw list-view names. Switch to plain English.",
            1 => "Words are shown in plain English. Switch to Xbox style button names.",
            _ => "Words are shown as Xbox style button names. Switch to the raw list-view names.",
        });
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
        // A yanked USB stick between FindCandidates and GetFiles is routine
        // for this hardware; it must never crash the home screen.
        var deviceFiles = Device.FindCandidatesCached()
            .SelectMany(root =>
            {
                try { return Directory.GetFiles(root, "*.csv"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                { return Array.Empty<string>(); }
            })
            .OrderBy(Path.GetFileName)
            .ToArray();
        DeviceEmptyText.IsVisible = deviceFiles.Length == 0;
        foreach (var path in deviceFiles)
            DeviceCards.Children.Add(ProfileCard(path, onDevice: true));
    }

    // ShowHome re-reads and re-parses every library + device file on each visit
    // just to show "N sheets, M bindings". Cache it by path + last-write time so
    // an unchanged file is parsed once, not on every navigation back to Home.
    // A slow USB still costs one read the first time it's seen; that's inherent.
    static readonly Dictionary<string, (long Stamp, string Sub)> _cardCache = new();

    static string CardSubtitle(string path)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch { stamp = 0; }
        if (_cardCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Sub;
        string sub;
        try
        {
            var doc = Parser.Parse(File.ReadAllText(path)).Doc;
            var bindings = doc.Sheets.Sum(s => s.Bindings.Count);
            sub = $"{doc.Sheets.Count} mode sheet(s), {bindings} binding(s)";
        }
        catch { sub = "Could not read this file"; }
        _cardCache[path] = (stamp, sub);
        return sub;
    }

    Control ProfileCard(string path, bool onDevice)
    {
        var name = Path.GetFileName(path);
        var subtitle = CardSubtitle(path);
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
        card.Click += async (_, _) =>
        {
            if (onDevice && name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase)
                && !await ConfirmAsync("Edit device preferences?",
                    "prefs.csv holds the QuadStick's own settings, not a game profile. A wrong value here changes how the whole device behaves. Only continue if you know which setting you are changing."))
                return;
            try
            {
                // Device files open as a working copy (no save path): Save
                // routes to the library, and only Install, with its backup and
                // verification, ever writes back to the QuadStick.
                OpenInEditor(ProfileFile.Load(File.ReadAllText(path)), onDevice ? null : path);
                if (onDevice)
                    Status("Opened from your QuadStick. Save keeps a copy in your library; use Install to put changes back on the device.", StatusKind.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stay on Home: showing an empty editor to display an error
                // strands the user in a dead view.
                HomeStatusText.Text = $"Could not open {name}: {ex.Message}";
                HomeStatusText.IsVisible = true;
            }
        };
        return card;
    }

    void NewFromTemplate() => OpenInEditor(ProfileFile.NewFromTemplate(DefaultNewName), savePath: null);

    /// <summary>First empty input cell (column C..J) on a binding row; 9 when the row is full.</summary>
    static int FirstFreeInputColumn(Binding b) =>
        Enumerable.Range(2, 8).FirstOrDefault(c => !b.InputCols.Contains(c), 9);

    void OpenInEditor(ProfileFile file, string? savePath)
    {
        _file = file;
        _savePath = savePath;
        _draftedRevision = -1; // new file: its Revision counter is unrelated to the last one's
        _sheetIndex = 0;
        RepopulateSheetPicker(0);
        FileNameBox.Text = file.Document.CsvFileName ?? "";
        var headerName = file.Document.HeaderName;
        Title = "Quadstick: Config Manager (unofficial) - "
            + (headerName.Length > 0 ? $"{headerName} ({file.Document.CsvFileName})" : file.Document.CsvFileName ?? "untitled");
        _selectedZone = null;
        ShowEditor();
        RefreshEditor(); // RefreshIssues inside sets the status line
    }

    void RepopulateSheetPicker(int select)
    {
        if (_file is null) return;
        SheetPicker.ItemsSource = _file.Document.Sheets
            .Select((s, i) => $"{i + 1}: {(s.ModeName.Length > 0 ? s.ModeName : s.Type.ToString())}")
            .ToList();
        SheetPicker.SelectedIndex = select;
    }

    // One dialog serves every "name a mode" prompt (add, rename, duplicate);
    // extraAboveBox slots caller-specific controls between the title and the
    // name box. Returns the trimmed name, or null on cancel or an empty name.
    async Task<string?> AskNameAsync(string title, string initialText, string confirmLabel,
        string boxAccessibleName, params Control[] extraAboveBox)
    {
        var box = new TextBox
        {
            Text = initialText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // A tester renamed a mode to a whole paragraph; nothing past this
            // fits the mode picker or the side tube's speech anyway.
            MaxLength = 40,
        };
        AutomationProperties.SetName(box, boxAccessibleName);
        var confirm = new Button { Content = confirmLabel, MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 140, IsCancel = true };
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 480,
        };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap });
        foreach (var extra in extraAboveBox) panel.Children.Add(extra);
        panel.Children.Add(box);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { confirm, cancel } });
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(panel, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        confirm.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        await dialog.ShowDialog(this);

        if (!confirmed) return null;
        var name = (box.Text ?? "").Trim();
        return name.Length == 0 ? null : name;
    }

    async Task AddModeAsync()
    {
        if (_file is null) { Status("Open or create a profile first."); return; }

        // "Start from" lists every existing mode so a new one can copy a
        // working layout instead of starting empty; index 0 is always blank.
        var sourceSheets = _file.Document.Sheets
            .Select((s, i) => (Sheet: s, Index: i))
            .Where(t => t.Sheet.Type == SheetType.ProfileName)
            .ToList();
        var startFromItems = new List<string> { "Blank mode" };
        startFromItems.AddRange(sourceSheets.Select(t => t.Sheet.ModeName));
        var startFromBox = new ComboBox
        {
            ItemsSource = startFromItems,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(startFromBox, "Start the new mode from");

        var name = await AskNameAsync("Add a mode", $"Mode {_file.Document.Sheets.Count + 1}",
            "Add mode", "Name for the new mode",
            new TextBlock { Text = "A mode is a second full layout of your inputs. Switch between modes while playing with the side tube or increment_mode / decrement_mode.", TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
            startFromBox);
        if (name is null) return;

        int idx;
        if (startFromBox.SelectedIndex > 0)
        {
            var source = sourceSheets[startFromBox.SelectedIndex - 1];
            idx = _file.DuplicateMode(source.Index, name);
            if (idx < 0) { Status("Could not add mode.", StatusKind.Error); return; }
        }
        else
        {
            idx = _file.AddModeSheet(name);
        }
        // SelectionChanged only fires when the index changes, so drive the
        // editor refresh explicitly (RefreshEditor is idempotent).
        _sheetIndex = idx;
        _selectedZone = null;
        RepopulateSheetPicker(idx);
        RefreshEditor();
    }

    // Built fresh on every click so enabled state always matches whichever
    // sheet is selected right now, not whatever was true when the app opened.
    void ShowModeMenu()
    {
        var sheets = _file?.Document.Sheets;
        var sheet = CurrentSheet;
        bool nameable = sheet != null && sheet.Type == SheetType.ProfileName;
        bool nextIsMode = sheets != null && _sheetIndex + 1 < sheets.Count
            && sheets[_sheetIndex + 1].Type == SheetType.ProfileName;
        bool prevIsMode = sheets != null && _sheetIndex > 0
            && sheets[_sheetIndex - 1].Type == SheetType.ProfileName;
        bool onlyOneMode = sheets != null && sheets.Count(s => s.Type == SheetType.ProfileName) <= 1;

        var rename = new MenuItem { Header = "Rename", IsEnabled = nameable };
        var duplicate = new MenuItem { Header = "Duplicate", IsEnabled = nameable };
        var moveUp = new MenuItem { Header = "Move up", IsEnabled = nameable && prevIsMode };
        var moveDown = new MenuItem { Header = "Move down", IsEnabled = nameable && nextIsMode };
        // Sheet 0 can never be deleted, but the item stays visible (just
        // disabled) so a user who lands on it sees why, not a missing option.
        // The Preferences sheet deletes too, for people who never use it.
        bool prefsSheet = sheet != null && sheet.Type == SheetType.Preferences;
        var delete = new MenuItem
        {
            Header = "Delete",
            IsEnabled = _sheetIndex != 0 && (prefsSheet || (nameable && !onlyOneMode)),
        };

        rename.Click += async (_, _) => await RenameModeAsync();
        duplicate.Click += async (_, _) => await DuplicateModeAsync();
        moveUp.Click += (_, _) => MoveCurrentMode(-1);
        moveDown.Click += (_, _) => MoveCurrentMode(1);
        delete.Click += async (_, _) => await DeleteModeAsync();

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(rename);
        menu.Items.Add(duplicate);
        menu.Items.Add(moveUp);
        menu.Items.Add(moveDown);
        menu.Items.Add(delete);
        if (sheets != null && !sheets.Any(s => s.Type == SheetType.Preferences))
        {
            var addPrefs = new MenuItem { Header = "Add preferences" };
            addPrefs.Click += (_, _) => AddPreferencesSheetToFile();
            menu.Items.Add(addPrefs);
        }
        menu.ShowAt(ModeMenuButton);
    }

    async Task RenameModeAsync()
    {
        if (_file is null) return;
        var sheet = CurrentSheet;
        if (sheet is null || sheet.Type != SheetType.ProfileName) return;

        var name = await AskNameAsync("Rename mode", sheet.ModeName, "Rename", "New name for this mode");
        if (name is null) return;
        if (_file.RenameMode(_sheetIndex, name))
        {
            RepopulateSheetPicker(_sheetIndex);
            Status("Mode renamed.", StatusKind.Ready);
        }
    }

    async Task DuplicateModeAsync()
    {
        if (_file is null) return;
        var sheet = CurrentSheet;
        if (sheet is null || sheet.Type != SheetType.ProfileName) return;

        var name = await AskNameAsync("Duplicate mode", sheet.ModeName + " copy",
            "Duplicate", "Name for the duplicated mode");
        if (name is null) return;
        int idx = _file.DuplicateMode(_sheetIndex, name);
        if (idx < 0) { Status("Could not duplicate mode.", StatusKind.Error); return; }
        _sheetIndex = idx;
        _selectedZone = null;
        RepopulateSheetPicker(idx);
        RefreshEditor();
        Status("Mode duplicated.", StatusKind.Ready);
    }

    void MoveCurrentMode(int delta)
    {
        if (_file is null) return;
        if (!_file.MoveMode(_sheetIndex, delta)) return;
        _sheetIndex += delta;
        _selectedZone = null;
        RepopulateSheetPicker(_sheetIndex);
        RefreshEditor();
        Status("Mode moved.", StatusKind.Ready);
    }

    void AddPreferencesSheetToFile()
    {
        if (_file is null) return;
        int idx = _file.AddPreferencesSheet();
        if (idx < 0) return;
        _sheetIndex = idx;
        _selectedZone = null;
        RepopulateSheetPicker(idx);
        RefreshEditor();
        Status("Preferences sheet added.", StatusKind.Ready);
    }

    async Task DeleteModeAsync()
    {
        if (_file is null) return;
        var sheet = CurrentSheet;
        if (sheet is null) return;
        bool prefs = sheet.Type == SheetType.Preferences;
        var name = prefs ? "Preferences" : sheet.ModeName.Length > 0 ? sheet.ModeName : "this mode";
        if (!await ConfirmAsync(prefs ? "Delete the Preferences sheet?" : "Delete this mode?",
            $"\"{name}\" and its rows are removed from the profile. Undo can bring it back."))
            return;
        if (!_file.DeleteMode(_sheetIndex)) return;
        _sheetIndex = Math.Min(_sheetIndex, _file.Document.Sheets.Count - 1);
        _selectedZone = null;
        RepopulateSheetPicker(_sheetIndex);
        RefreshEditor();
        Status("Mode deleted.", StatusKind.Ready);
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
        // Avalonia calls item templates with a null item during measure, so a
        // null token can reach here before any real value is bound.
        if (string.IsNullOrEmpty(input)) return "(no input)";
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

    // PlayStation output tokens shown with their Xbox equivalents, for users
    // who think in Xbox terms. Only game buttons differ; everything else
    // (keyboard, mouse, dpad, sticks) reads the same on both and falls back
    // to plain English.
    internal static readonly Dictionary<string, string> XboxStyle = new(StringComparer.Ordinal)
    {
        ["x"] = "A button", ["circle"] = "B button", ["square"] = "X button",
        ["triangle"] = "Y button", ["left_1"] = "Left bumper", ["right_1"] = "Right bumper",
        ["left_2"] = "Left trigger", ["right_2"] = "Right trigger",
        ["left_3"] = "Left stick click", ["right_3"] = "Right stick click",
        ["select"] = "View button", ["start"] = "Menu button", ["ps3"] = "Xbox button",
    };

    // How an output/function token is shown in Device View: friendly words,
    // Xbox-style names, or the raw token exactly as List View and the CSV
    // spell it.
    string TokenLabel(string token)
    {
        // Avalonia templates a null item during measure before any value binds.
        var t = token ?? "";
        return _labelStyle == 0 ? t
            : _labelStyle == 2 && XboxStyle.TryGetValue(t, out var xbox) ? xbox
            : Humanize(t);
    }

    // Label for an input token in a dropdown that can list inputs from more than
    // one part. Same-part tokens read bare ("Puff"); tokens from another part are
    // qualified ("Left · puff") so three parts' "puff" don't collapse into three
    // identical-looking rows.
    string InputOptionLabel(string token, string cardZone)
    {
        // Avalonia templates a null item during measure before any value binds.
        if (string.IsNullOrEmpty(token) || !_friendlyLabels) return token ?? "";
        var tz = ZoneOf(token);
        var bare = Humanize(StripInput(token, tz));
        if (bare.Length == 0) return bare;
        var low = $"{char.ToLowerInvariant(bare[0])}{bare[1..]}";
        // Four hole pairings all strip to the same word ("sip"), so a combo
        // token must always name its pairing or the list reads as duplicates.
        if (tz == "combo")
        {
            var pair = token.StartsWith("mp_triple_") ? "All three"
                : token.StartsWith("mp_left_center_") ? "Left + Center"
                : token.StartsWith("mp_right_center_") ? "Right + Center"
                : token.StartsWith("mp_left_right_") ? "Left + Right" : "Combo";
            return $"{pair} · {low}";
        }
        if (tz == cardZone || tz is "other" or "unset") return bare;
        var disp = AllZones.FirstOrDefault(z => z.Id == tz)?.Display ?? tz;
        return $"{disp} · {low}";
    }

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
        DeviceViewButton.Classes.Set("primary", device && !_railView);
        RailViewButton.Classes.Set("primary", device && _railView);
        ListViewButton.Classes.Set("primary", !device);
        // The words toggle only changes Device View labels; List View already
        // shows the raw names, so hide it there rather than offer a dead control.
        LabelStyleButton.IsVisible = device;
        var connected = Device.FindCandidatesCached().Count > 0;
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

    // The "refactor": NOT an incremental diff engine. Device View still rebuilds
    // from truth on every edit (small profiles, blur-triggered commits — a diff
    // would only add stale-UI and focus bugs). What was missing is focus: after
    // a rebuild the control the user just used is gone, so keyboard/switch users
    // were dropped. Rebuild, then refocus the same cell's replacement control.
    // Editing an input can move its mapping to another zone, so follow it there.
    void RebuildDeviceAfterEdit(int row, int col)
    {
        if (col >= 2 && _file is not null) // an input cell: its zone follows the new value
            _selectedZone = ZoneOf(_file.GetCell(row, col));
        BuildDeviceView();
        BuildZoneDetail();
        RefreshIssues();
        AfterLayout(() =>
        {
            if (_cellBorders.TryGetValue($"{(char)('A' + col)}{row}", out var border))
            { border.BringIntoView(); (border.Child as Control)?.Focus(); }
        });
    }

    void BuildDeviceView()
    {
        DeviceCanvas.Children.Clear();
        _zoneButtons.Clear();
        _cellBorders.Clear(); // stale entries from other zones/profiles would get issue-highlighted
        var byZone = BindingsByZone();

        // Parts List: a plain vertical list of part rows instead of the diagram,
        // for users who'd rather arrow through a list than read a picture.
        if (_railView)
        {
            var rail = new StackPanel { Spacing = 6 };
            rail.Children.Add(new TextBlock
            { Text = "Parts", FontSize = Size("SmallSize"), FontWeight = FontWeight.Bold, Classes = { "muted" }, Margin = new Avalonia.Thickness(2, 0, 0, 4) });
            foreach (var z in VisibleZones(byZone))
                rail.Children.Add(RailRow(z, byZone));
            DeviceCanvas.Children.Add(rail);
            return;
        }

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
        var extras = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 18, 0, 0) };
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

        // Every foreign part says so in TEXT, circles included: dimming alone
        // is a contrast-only cue that low-vision users cannot rely on.
        if (foreign)
            content.Children.Add(new TextBlock
            {
                Text = circle ? "Not on model" : "Not on your model",
                FontSize = Size("SmallSize"), Classes = { "muted" },
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

        SetZoneAccessibleName(btn, z, bindings, count, foreign, selected);
        WireZoneSelect(btn, z.Id);
        return btn;
    }

    // A part row for the Parts List view: the same selectable control as a
    // diagram tile, laid out as a wide row (name + mapping count) so the left
    // side becomes a plain list to arrow through. Feeds the same editor.
    Control RailRow(Zone z, Dictionary<string, List<Binding>> byZone)
    {
        byZone.TryGetValue(z.Id, out var bindings);
        int count = bindings?.Count ?? 0;
        bool foreign = !ModelHasZone(z.Id);
        bool selected = _selectedZone == z.Id;

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = z.Title, FontWeight = FontWeight.Bold, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap });
        if (foreign)
            name.Children.Add(new TextBlock { Text = "Not on your model", FontSize = Size("SmallSize"), Classes = { "muted" } });

        var cnt = new TextBlock
        {
            Text = count == 0 ? "Not mapped" : count == 1 ? "1 mapping" : $"{count} mappings",
            FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
        };
        if (count == 0) cnt.Classes.Add("muted"); else BindBrush(cnt, TextBlock.ForegroundProperty, "AccentText");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);
        Grid.SetColumn(cnt, 1);
        row.Children.Add(cnt);

        var btn = new ToggleButton
        {
            Classes = { "zone" }, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Avalonia.Thickness(14, 12), Content = row, IsChecked = selected,
        };
        if (foreign) btn.Opacity = 0.5;
        SetZoneAccessibleName(btn, z, bindings, count, foreign, selected);
        WireZoneSelect(btn, z.Id);
        return btn;
    }

    void SetZoneAccessibleName(Control btn, Zone z, List<Binding>? bindings, int count, bool foreign, bool selected)
    {
        var spoken = count == 0
            ? "nothing mapped yet"
            : string.Join(", ", (bindings ?? new()).Take(4).Select(b => $"{ShortInput(z, b)} presses {b.Output}"));
        var warning = foreign ? $" Not available on your {ModelNames[(int)_model]}." : "";
        AutomationProperties.SetName(btn,
            $"{z.Title}. {(selected ? "Selected. " : "")}{count} mapping{(count == 1 ? "" : "s")}. {spoken}.{warning} Press Enter to edit.");
    }

    void WireZoneSelect(ToggleButton btn, string zoneId)
    {
        btn.Click += (_, _) =>
        {
            _selectedZone = zoneId;
            _selectedRows.Clear(); _selAnchor = -1; // cards of another part leave the screen
            BuildDeviceView(); BuildZoneDetail();
            // The click target no longer exists after the rebuild above; refocus
            // its replacement so keyboard/switch users aren't dropped. IsChecked
            // is re-derived from _selectedZone on rebuild, so re-clicking the
            // selected part can't leave it unchecked with no selection.
            _zoneButtons.GetValueOrDefault(zoneId)?.Focus();
        };
        _zoneButtons[zoneId] = btn;
    }

    // Card view state: the one mapping open for editing; -1 = all cards closed.
    int _expandedMapping = -1;

    void UpdateCardViewButton()
    {
        CardViewButton.Content = _settings.DeviceCards ? "Detailed editor" : "Simple cards";
        AutomationProperties.SetName(CardViewButton, _settings.DeviceCards
            ? "Mappings read as simple sentence cards. Switch to the detailed editor."
            : "Mappings show the detailed editor. Switch to simple sentence cards.");
    }

    // One mapping as a plain sentence: "Decrement mode when you soft puff, as
    // normal." The output and inputs wear their column colors, the note reads
    // as a muted second line, and a click opens the detailed editor for just
    // this mapping. The handle on the left selects and drags, exactly like a
    // list-view row number.
    Control SentenceCard(Zone zone, Binding b, int n)
    {
        Control Pill(string text, string tint)
        {
            var bd = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(4), Padding = new Avalonia.Thickness(7, 2),
                Margin = new Avalonia.Thickness(0, 2), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = Size("BodySize"), FontWeight = FontWeight.SemiBold },
            };
            BindBrush(bd, Border.BackgroundProperty, tint);
            return bd;
        }
        Control Word(string text) => new TextBlock
        {
            Text = text, FontSize = Size("BodySize"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(5, 2),
        };

        // The same Words button styles as everywhere else in device view.
        string output = b.Output.Length > 0 ? TokenLabel(b.Output) : "(nothing yet)";
        var inputs = b.Inputs.Count > 0
            ? b.Inputs.Select(i => _labelStyle == 0 ? i : StripInput(i, zone.Id)).ToList()
            : new List<string> { "(no input)" };
        string func = _labelStyle == 0 ? b.Function : b.Function.Replace('_', ' ');

        var line = new WrapPanel();
        line.Children.Add(Pill(output, OutputTint));
        line.Children.Add(Word("when you"));
        for (int i = 0; i < inputs.Count; i++)
        {
            if (i > 0) line.Children.Add(Word("and"));
            line.Children.Add(Pill(inputs[i], InputTint));
        }
        if (func.Length > 0)
        {
            line.Children.Add(Word("as"));
            line.Children.Add(Pill(func, FunctionTint));
        }

        var body = new StackPanel { Spacing = 4, Children = { line } };
        var note = _file!.GetCell(b.Row, NoteColumn);
        if (note.Length > 0)
            body.Children.Add(new TextBlock
            { Text = note, FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });

        var open = new Button
        {
            Content = body, Classes = { "quiet" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Avalonia.Thickness(10, 8),
        };
        AutomationProperties.SetName(open,
            $"Mapping {n}: {output} when you {string.Join(" and ", inputs)}" +
            $"{(func.Length > 0 ? $", as {func}" : "")}. Press Enter to edit.");
        open.Click += (_, _) =>
        {
            _expandedMapping = b.Row;
            BuildZoneDetail();
            AfterLayout(() =>
            {
                if (_cellBorders.TryGetValue($"C{b.Row}", out var border))
                { border.BringIntoView(); (border.Child as Control)?.Focus(); }
            });
        };

        // 40x40 is the floor for a click target here (see Button.icon); the
        // tester found the old 24px-wide strip too small to hit.
        var dragIcon = Glyph("IconDrag", "TextSecondary");
        dragIcon.Width = dragIcon.Height = 24;
        var handle = WireDragHandle(new Border
        { Child = dragIcon, Padding = new Avalonia.Thickness(10) },
            b, $"Mapping {n}");

        var p = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        p.Children.Add(handle);
        Grid.SetColumn(open, 1);
        p.Children.Add(open);
        WireRowDrop(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);
        return p;
    }

    void BuildZoneDetail()
    {
        ZoneDetailPanel.Children.Clear();
        _rowPanels.Clear(); // device view owns the selection targets while visible
        var zone = AllZones.FirstOrDefault(z => z.Id == _selectedZone);
        if (zone is null)
        {
            ZoneDetailPanel.Children.Add(new TextBlock
            {
                Text = "Nothing selected.\n\nPick a part of the QuadStick on the left to see what it does in this mode, change it, or map something new to it.",
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            RepaintSelection();
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
            bool cards = _settings.DeviceCards;
            int n = 0;
            foreach (var b in bindings)
            {
                n++;
                // Card mode: a closed mapping is one readable sentence. Only
                // the expanded one (at most one, accordion style) gets the
                // full editor below.
                if (cards && b.Row != _expandedMapping)
                { ZoneDetailPanel.Children.Add(SentenceCard(zone, b, n)); continue; }
                // One compact card per mapping. A header line carries the number
                // and a small remove button; the body is three aligned label|field
                // rows ("When you / Press / As") so a mapping reads like a short
                // sentence instead of a tall stack of separate labelled boxes.
                var body = new StackPanel { Spacing = 6 };

                // The header is just the actions, right-aligned: Done (card
                // mode only) and a big trash icon. The card already says
                // which mapping this is, a "Mapping N" label repeated it.
                var header = new StackPanel
                { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
                var delIcon = Glyph("IconDelete", "Error");
                delIcon.Width = delIcon.Height = 32; // double the usual 16, per the tester
                var del = new Button { Classes = { "danger", "quiet" }, Padding = new Avalonia.Thickness(8, 2), Content = delIcon };
                ToolTip.SetTip(del, "Remove this mapping");
                AutomationProperties.SetName(del, $"Remove the {ShortInput(zone, b)} mapping");
                del.Click += (_, _) =>
                {
                    int deletedIndex = bindings!.IndexOf(b);
                    _file!.DeleteRow(b.Row);
                    BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                    FocusZoneDetailSibling(zone.Id, deletedIndex);
                };
                if (cards)
                {
                    // The way back to the sentence card, next to the trash.
                    var done = new Button { Content = "Done", Classes = { "quiet" }, Padding = new Avalonia.Thickness(12, 2) };
                    AutomationProperties.SetName(done, $"Close the editor for mapping {n} and go back to its card");
                    done.Click += (_, _) => { _expandedMapping = -1; BuildZoneDetail(); };
                    header.Children.Add(done);
                }
                header.Children.Add(del);
                body.Children.Add(header);

                // ---- "When you": one aligned row per input, each removable ----
                var inputsBox = new StackPanel { Spacing = 6 };
                int inputCount = Math.Max(1, b.Inputs.Count);
                for (int i = 0; i < inputCount && i < 8; i++)
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    // Inputs can sit in any of columns C..J with gaps; the
                    // editor must write to each input's REAL column, or an
                    // edit lands on a blank cell and duplicates the input.
                    int col = i < b.InputCols.Count ? b.InputCols[i] : FirstFreeInputColumn(b);
                    var inputBox = TokenField(b.Row, col, i < b.Inputs.Count ? b.Inputs[i] : "",
                        i == 0 && zoneInputs.Count > 0 ? zoneInputs : InputSuggestions,
                        t => InputOptionLabel(t, zone.Id),
                        $"Input {i + 1} for this {zone.Display} mapping", InputTint);
                    Grid.SetColumn(inputBox, 0);
                    row.Children.Add(inputBox);
                    // Every committed input gets a trash. Removing the last one
                    // leaves an empty box on purpose — that IS the "no input" state.
                    if (i < b.Inputs.Count)
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
                        row.Children.Add(rmv);
                    }
                    inputsBox.Children.Add(row);
                }
                if (inputCount < 8)
                {
                    var addInput = IconButton("IconAdd", $"Add another input to mapping {n}; both inputs must be active together");
                    addInput.HorizontalAlignment = HorizontalAlignment.Left;
                    ToolTip.SetTip(addInput, "Add another input");
                    int nextCol = FirstFreeInputColumn(b);
                    addInput.Click += (_, _) =>
                    {
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                        var newBox = TokenField(b.Row, nextCol, "", InputSuggestions,
                            t => InputOptionLabel(t, zone.Id), $"Extra input for mapping {n}", InputTint);
                        Grid.SetColumn(newBox, 0);
                        row.Children.Add(newBox);
                        // The new row isn't committed until a value is picked, so its
                        // trash just drops the row instead of editing the file.
                        var rmv = IconButton("IconDelete", $"Remove this empty input from mapping {n}");
                        rmv.Margin = new Avalonia.Thickness(8, 0, 0, 0);
                        rmv.Click += (_, _) => inputsBox.Children.Remove(row);
                        Grid.SetColumn(rmv, 1);
                        row.Children.Add(rmv);
                        inputsBox.Children.Insert(inputsBox.Children.IndexOf(addInput), row);
                        nextCol++;
                        if (nextCol >= 2 + 8) addInput.IsVisible = false;
                        (newBox as Border)?.Child?.Focus();
                    };
                    inputsBox.Children.Add(addInput);
                }
                body.Children.Add(Labeled("When you", inputsBox));

                // ---- "Press" (game button) and "As" (how it presses) ----
                body.Children.Add(Labeled("Press", TokenField(b.Row, 0, b.Output, OutputSuggestionsFor(CurrentSheet!),
                    TokenLabel, $"Game button pressed by {ShortInput(zone, b)}", OutputTint)));
                body.Children.Add(Labeled("As", FunctionCombo(b, zone)));
                body.Children.Add(Labeled("Note", NoteBox(b.Row, NoteColumn, $"Note for this mapping. Saved in the file, ignored by the QuadStick")));

                var mappingCard = new Border
                {
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(14),
                    Child = body,
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
                _expandedMapping = newRow; // a brand new mapping opens ready to edit
                BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
                // Take the user to the mapping they just created (mirrors AddRow in List View).
                // The new input cell is a ComboBox (TokenField), so focus it as a Control.
                AfterLayout(() =>
                {
                    if (_cellBorders.TryGetValue($"C{newRow}", out var newBorder))
                    { newBorder.BringIntoView(); (newBorder.Child as Control)?.Focus(); }
                });
            };
            ZoneDetailPanel.Children.Add(add);
        }
        RepaintSelection(); // the bars follow whatever rebuilt here
    }

    public void LoadProfile(ProfileFile file) => OpenInEditor(file, savePath: null);

    public void SelectZoneForPreview(string zoneId)
    { _selectedZone = zoneId; BuildDeviceView(); BuildZoneDetail(); }

    public void SetModelForPreview(int index)
    { ModelPicker.SelectedIndex = index; }

    public void SetDeviceViewForPreview(bool device) => SetDeviceView(device);

    public void CycleLabelStyleForPreview() => ToggleLabelStyle();

    public void ShowProblemsForPreview()
    { if (!_problemsExpanded) ToggleProblems(); }

    // Async click/shortcut handlers are fire-and-forget; an unhandled disk
    // error would otherwise tear down the whole app.
    async Task GuardedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status(ex.Message, StatusKind.Error); }
    }

    async Task OpenAsync()
    {
        if (!await ConfirmLeaveAsync()) return; // opening discards unsaved work
        var picks = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open QuadStick profile",
            FileTypeFilter = new[] { new FilePickerFileType("QuadStick profile CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (picks.Count == 0) return;
        try
        {
            OpenInEditor(ProfileFile.Load(await File.ReadAllTextAsync(picks[0].Path.LocalPath)), picks[0].Path.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status($"Could not open {picks[0].Name}: {ex.Message}", StatusKind.Error); }
    }

    // Returns true only once the file has actually reached disk, so
    // ConfirmLeaveAsync can tell a real save from a cancelled picker or a
    // failed write and keep the user's work on screen either way.
    async Task<bool> SaveAsync()
    {
        if (_file is null) return false;

        // Save never writes to the QuadStick itself. Only Install does: it is
        // the one path with validate, backup, readback, and the default.csv
        // confirmation. Without this gate, opening default.csv from its own
        // device card and pressing Ctrl+S would overwrite the fallback file raw.
        if (_savePath is not null
            && Path.GetDirectoryName(_savePath) is string dir
            && Device.IsInstallTarget(dir))
        {
            Status("This profile lives on the QuadStick. Use Install to write it back safely; Save As puts a copy in your library.", StatusKind.Warning);
            _savePath = null; // fall through to Save As on the next save
            return false;
        }

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
            if (pick is null) return false;
            var pickedDir = Path.GetDirectoryName(pick.Path.LocalPath);
            if (pickedDir is not null && Device.IsInstallTarget(pickedDir))
            {
                Status("That folder is a QuadStick drive. Use Install to write to the device safely.", StatusKind.Warning);
                return false;
            }
            _savePath = pick.Path.LocalPath;
        }

        try
        {
            _file.EnsureVersionHeader(); // saved files match installed files byte for byte
            await File.WriteAllTextAsync(_savePath, _file.ToCsvText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status($"Could not save: {ex.Message}", StatusKind.Error); return false; }
        _file.Dirty = false;
        RefreshEditor(); // header insertion shifted every row; BOTH views must rebind
        Status($"Saved to {_savePath}.", StatusKind.Ready);
        return true;
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

    // Strip characters that are illegal in a file name and force a .csv
    // extension, so a template named "My FPS / v2" cannot escape TemplatesDir
    // or land without an extension the loader looks for.
    public static string SafeTemplateName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        // Strip this platform's invalid chars, plus the path separators and
        // drive colon that are legal on macOS but break a synced file on
        // Windows. This app runs on both, so a template name must be safe on
        // both.
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        var cleaned = string.Concat(trimmed.Select(c => invalid.Contains(c) ? '_' : c));
        if (cleaned.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^4];
        return cleaned + ".csv";
    }

    async Task SaveAsTemplateAsync()
    {
        if (_file is null) { Status("Open or create a profile first."); return; }

        var suggested = Path.GetFileNameWithoutExtension(_file.Document.CsvFileName ?? "my template");
        var box = new TextBox { Text = suggested, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, "Name for this template");
        var save = new Button { Content = "Save template", MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 140, IsCancel = true };
        var dialog = new Window
        {
            Title = "Save as template",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = "Save as template", FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Keeps a copy you can start new profiles from any time, under Use template on the home screen. Editing or installing a profile never changes its template.", TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } },
                },
            }, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        save.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        await dialog.ShowDialog(this);
        if (!confirmed) return;

        var fileName = SafeTemplateName(box.Text ?? "");
        if (fileName.Length == 0) { Status("A template needs a name.", StatusKind.Warning); return; }
        try
        {
            Directory.CreateDirectory(TemplatesDir);
            _file.EnsureVersionHeader(); // templates match installed files byte for byte
            await File.WriteAllTextAsync(Path.Combine(TemplatesDir, fileName), _file.ToCsvText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status($"Could not save the template: {ex.Message}", StatusKind.Error); return; }
        RefreshEditor(); // EnsureVersionHeader may have shifted rows
        Status($"Saved template {fileName}. Find it under Use template on the home screen.", StatusKind.Ready);
    }

    async Task UseTemplateAsync()
    {
        void HomeError(string message)
        { HomeStatusText.Text = message; HomeStatusText.IsVisible = true; }
        HomeStatusText.IsVisible = false;

        var templates = Directory.Exists(TemplatesDir)
            ? Directory.GetFiles(TemplatesDir, "*.csv").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        if (templates.Length == 0)
        { HomeError("You have not saved any templates yet. Open a profile and use Save as template to make one."); return; }

        if (!await ConfirmLeaveAsync()) return; // opening discards unsaved work

        // Mutable so Rename/Delete can update the picker in place without
        // closing and reopening the dialog.
        var templatePaths = templates.ToList();
        var list = new ListBox
        {
            ItemsSource = templatePaths.Select(Path.GetFileNameWithoutExtension).ToList(),
            SelectedIndex = 0,
            MaxHeight = 320,
        };
        AutomationProperties.SetName(list, "Your saved templates");
        void RefreshList(int selectIndex)
        {
            list.ItemsSource = templatePaths.Select(Path.GetFileNameWithoutExtension).ToList();
            list.SelectedIndex = selectIndex;
        }

        var open = new Button { Content = "Use template", MinWidth = 140, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 140, IsCancel = true };
        var rename = new Button { Content = "Rename", Classes = { "quiet" } };
        var delete = new Button { Content = "Delete", Classes = { "danger", "quiet" } };
        AutomationProperties.SetName(rename, "Rename selected template");
        AutomationProperties.SetName(delete, "Delete selected template");
        var dialog = new Window
        {
            Title = "Use template",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = "Start from a template", FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Opens a fresh copy you can edit and install. Your template stays as it is.", TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { rename, delete } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { open, cancel } },
                },
            }, _uiScale),
        };
        var confirmed = false;
        void Confirm() { confirmed = true; dialog.Close(); }
        open.Click += (_, _) => Confirm();
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => Confirm();

        rename.Click += async (_, _) =>
        {
            var idx = list.SelectedIndex;
            if (idx < 0) { Status("Select a template to rename first.", StatusKind.Warning); return; }
            var oldPath = templatePaths[idx];
            var newName = await AskNameAsync("Rename template", Path.GetFileNameWithoutExtension(oldPath),
                "Rename", "New name for this template");
            if (newName is null) return;
            var fileName = SafeTemplateName(newName);
            if (fileName.Length == 0) { Status("A template needs a name.", StatusKind.Warning); return; }
            var newPath = Path.Combine(TemplatesDir, fileName);
            if (!string.Equals(newPath, oldPath, StringComparison.Ordinal))
            {
                if (File.Exists(newPath))
                { Status($"A template named {Path.GetFileNameWithoutExtension(fileName)} already exists.", StatusKind.Warning); return; }
                try { File.Move(oldPath, newPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Status($"Could not rename the template: {ex.Message}", StatusKind.Error); return; }
                templatePaths[idx] = newPath;
            }
            RefreshList(idx);
            Status($"Renamed template to {Path.GetFileNameWithoutExtension(fileName)}.", StatusKind.Ready);
        };

        delete.Click += async (_, _) =>
        {
            var idx = list.SelectedIndex;
            if (idx < 0) { Status("Select a template to delete first.", StatusKind.Warning); return; }
            var targetPath = templatePaths[idx];
            var name = Path.GetFileNameWithoutExtension(targetPath);
            if (!await ConfirmAsync($"Delete template \"{name}\"?",
                "Profiles you already made from this template are not affected. This cannot be undone."))
                return;
            try { File.Delete(targetPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status($"Could not delete the template: {ex.Message}", StatusKind.Error); return; }
            templatePaths.RemoveAt(idx);
            if (templatePaths.Count == 0)
            {
                dialog.Close();
                HomeError("You have not saved any templates yet. Open a profile and use Save as template to make one.");
                Status($"Deleted template {name}.", StatusKind.Ready);
                return;
            }
            RefreshList(Math.Min(idx, templatePaths.Count - 1));
            Status($"Deleted template {name}.", StatusKind.Ready);
        };

        await dialog.ShowDialog(this);
        if (!confirmed || list.SelectedIndex < 0) return;

        var path = templatePaths[list.SelectedIndex];
        try
        {
            // savePath null: the copy is unsaved, so Save prompts for a new
            // location and the template file is never overwritten.
            OpenInEditor(ProfileFile.Load(await File.ReadAllTextAsync(path)), savePath: null);
            Status($"Started from template {Path.GetFileNameWithoutExtension(path)}. Save to keep this as its own profile.", StatusKind.Ready);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { HomeError($"Could not open that template: {ex.Message}"); }
    }

    bool _closeConfirmed;

    void CommitFileName()
    {
        if (_file is null) return;
        var v = (FileNameBox.Text ?? "").Trim();
        if (v.Length == 0 || v == _file.Document.CsvFileName) return;
        _file.SetCell(_file.Document.FileNameCellRow, 0, v);
        Title = $"Quadstick: Config Manager (unofficial) - {v}";
        RefreshIssues(); // bad names surface immediately as errors
    }

    // Returning true means it is safe to discard the open profile and
    // proceed. Save only earns that if SaveAsync actually reached disk; a
    // cancelled picker or a failed write must keep the user right where
    // they were, work intact.
    async Task<bool> ConfirmLeaveAsync()
    {
        if (_file is not { Dirty: true }) return true;

        var title = "Save your changes?";
        var message = "This profile has unsaved changes. Save them before leaving?";
        var save = new Button { Content = "Save", MinWidth = 140, IsDefault = true };
        var dontSave = new Button { Content = "Don't save", MinWidth = 140 };
        var cancel = new Button { Content = "Cancel", MinWidth = 140, IsCancel = true };
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
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, dontSave, cancel } },
                },
            }, _uiScale),
        };
        var choice = "cancel";
        save.Click += (_, _) => { choice = "save"; dialog.Close(); };
        dontSave.Click += (_, _) => { choice = "dontsave"; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);

        switch (choice)
        {
            case "save": return await SaveAsync(); // a blocked or cancelled save must not leave
            case "dontsave":
                // An explicit "don't save" is a discard: drop the autosave
                // draft too, or the next launch offers back the exact work
                // the user just declined to keep.
                try { File.Delete(DraftPath); } catch { /* best effort */ }
                _draftedRevision = -1;
                return true;
            default: return false;
        }
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
        _rowPanels.Clear();
        if (CurrentSheet is null) { RefreshIssues(); return; }

        // Selected rows that left the sheet (deleted, or another sheet is
        // showing now) must not tint whatever row wears their number next.
        _selectedRows.RemoveWhere(r => CurrentSheet.Bindings.All(x => x.Row != r));

        bool prefs = CurrentSheet.Type != SheetType.ProfileName;
        RowsPanel.Children.Add(prefs ? PrefsHeaderRow() : HeaderRow());
        int number = 1;
        foreach (var b in CurrentSheet.Bindings)
            RowsPanel.Children.Add(prefs ? PrefsRow(b, number++) : BindingRow(b, number++));

        if (CurrentSheet.Bindings.Count == 0)
            RowsPanel.Children.Add(new TextBlock
            {
                Text = prefs
                    ? "No settings on this sheet yet. Click \"Add row\" to add one."
                    : "No bindings yet. Click \"Add row\" to connect an input to an output.",
                FontSize = Size("BodySize"), Classes = { "muted" }, Margin = new Avalonia.Thickness(4, 12),
            });
        RepaintSelection(); // the prune above may have emptied it; the bar must follow
        RefreshIssues();
    }

    // A tinted, rounded column header used by both the bindings and preferences
    // header rows.
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

    // How wide the row-number column is, in RowNumberLabel and its matching
    // header spacer. Fixed so every row's Output cell lines up under the
    // Output swatch no matter how many digits the row number has.
    const double RowNumberWidth = 34;

    // The line's position in the visible list (1, 2, 3...), shown at the left
    // edge. Not the CSV grid row: bindings start three header lines down, so
    // the raw row would begin at 4 and read as wrong.
    static Control RowNumberLabel(int number) => new TextBlock
    {
        Text = number.ToString(), FontSize = Size("SmallSize"), Classes = { "muted" },
        Width = RowNumberWidth, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Right, Margin = new Avalonia.Thickness(0, 0, 4, 0),
    };

    // Keeps the header swatches lined up over their columns now that every
    // data row starts with a row-number label.
    static Control RowNumberHeaderSpacer() => new Border
    { Width = RowNumberWidth, Margin = new Avalonia.Thickness(0, 0, 4, 0) }; // same margin as the label

    // List View rows reorder by dragging their row number onto another row;
    // the dragged row takes the drop target's place. The move chevrons stay:
    // dragging is exactly what some QuadStick users cannot do.
    const string RowDragFormat = "qcm-grid-row";

    // Rows selected by clicking their numbers, file-explorer style: click
    // picks one, Ctrl/Cmd toggles, Shift extends from the anchor, Escape or
    // a click on empty space clears. Keyed by CSV row on the current sheet.
    readonly HashSet<int> _selectedRows = new();
    int _selAnchor = -1;
    readonly Dictionary<int, Panel> _rowPanels = new();

    // The rows the user can actually see and select right now: only this
    // part's mappings in device view, the whole mode in list view. Shift
    // ranges and the Move menu must never reach rows outside this list.
    List<int> VisibleSelectableRows()
    {
        if (DeviceContainer.IsVisible && _selectedZone is { } z)
            return BindingsByZone().TryGetValue(z, out var bs)
                ? bs.Select(x => x.Row).ToList() : new List<int>();
        return CurrentSheet is { } sheet
            ? sheet.Bindings.Select(x => x.Row).ToList() : new List<int>();
    }

    void PaintRow(int row)
    {
        if (!_rowPanels.TryGetValue(row, out var p)) return;
        bool sel = _selectedRows.Contains(row);
        if (sel) BindBrush(p, Panel.BackgroundProperty, "SelectionTint");
        else p.ClearValue(Panel.BackgroundProperty);
        if (p.Children[0] is Border h && h.Tag is string baseName)
            AutomationProperties.SetName(h,
                $"{baseName}{(sel ? ", selected" : "")}. Space selects, drag reorders");
    }

    void RepaintSelection()
    {
        foreach (var row in _rowPanels.Keys) PaintRow(row);
        SelectionBar.IsVisible = _selectedRows.Count > 0 && !DeviceContainer.IsVisible;
        SelectionCount.Text = $"{_selectedRows.Count} selected";
        DeviceSelectionBar.IsVisible = _selectedRows.Count > 0 && DeviceContainer.IsVisible;
        DeviceSelectionCount.Text = SelectionCount.Text;
    }

    void DeleteSelectedRows()
    {
        if (_file is null || _selectedRows.Count == 0) return;
        var rows = _selectedRows.ToList();
        _selectedRows.Clear(); _selAnchor = -1;
        var off = GridScroll.Offset;
        _file.DeleteRows(rows); // one undo step for the whole selection
        if (DeviceContainer.IsVisible) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else
        {
            RebuildRows();
            RestoreListScroll(off, () => { });
        }
        Status($"{rows.Count} row{(rows.Count == 1 ? "" : "s")} deleted. Ctrl+Z brings them back.", StatusKind.Ready);
    }

    void ClearSelection()
    {
        if (_selectedRows.Count == 0) return;
        _selectedRows.Clear();
        _selAnchor = -1;
        RepaintSelection();
    }

    MenuFlyout MoveMenu()
    {
        var top = new MenuItem { Header = "To the top" };
        top.Click += (_, _) => MoveSelection(top: true);
        var bottom = new MenuItem { Header = "To the bottom" };
        bottom.Click += (_, _) => MoveSelection(top: false);
        return new MenuFlyout { Items = { top, bottom } };
    }

    void MoveSelection(bool top)
    {
        if (_file is null || _selectedRows.Count == 0) return;
        // Land against the first or last row the user can see that is not
        // already selected; with everything selected there is nowhere to go.
        var anchors = VisibleSelectableRows().Where(r => !_selectedRows.Contains(r)).ToList();
        if (anchors.Count == 0) return;
        var srcs = _selectedRows.OrderBy(r => r).ToArray();
        _selectedRows.Clear(); _selAnchor = -1; // rows renumber under a stale selection
        var off = GridScroll.Offset;
        if (top) _file.MoveRowsBefore(srcs, anchors[0]);
        else _file.MoveRowsAfter(srcs, anchors[^1]);
        if (DeviceContainer.IsVisible) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else
        {
            RebuildRows();
            RestoreListScroll(off, () => { });
        }
        Status($"{srcs.Length} row{(srcs.Length == 1 ? "" : "s")} moved to the {(top ? "top" : "bottom")}. Ctrl+Z undoes it.", StatusKind.Ready);
    }

    void SelectFromClick(int row, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
        { if (!_selectedRows.Remove(row)) _selectedRows.Add(row); _selAnchor = row; }
        else if (mods.HasFlag(KeyModifiers.Shift) && _selAnchor >= 0)
        {
            var rows = VisibleSelectableRows();
            int a = rows.IndexOf(_selAnchor), z = rows.IndexOf(row);
            if (a < 0) a = z;
            _selectedRows.Clear();
            for (int i = Math.Min(a, z); i <= Math.Max(a, z); i++) _selectedRows.Add(rows[i]);
        }
        // A plain click on an already-selected row keeps the whole set, so a
        // multi-row drag can start from any of its rows.
        else if (!_selectedRows.Contains(row))
        { _selectedRows.Clear(); _selectedRows.Add(row); _selAnchor = row; }
        else _selAnchor = row;
        RepaintSelection();
    }

    Control DragHandle(Binding b, int number) =>
        WireDragHandle(new Border { Child = RowNumberLabel(number) }, b, $"Row {number}");

    // Shared by the list-view row numbers and the device-view card handles:
    // click selects (with Ctrl/Cmd/Shift), Space selects, a real movement
    // starts a drag carrying the whole selection.
    Border WireDragHandle(Border h, Binding b, string baseName)
    {
        h.Background = Brushes.Transparent; // hit-testable everywhere
        h.Cursor = new Cursor(StandardCursorType.SizeAll);
        h.VerticalAlignment = VerticalAlignment.Center;
        h.Focusable = true; // Space selects for keyboard and switch users
        h.Tag = baseName;   // PaintRow appends ", selected" to this
        ToolTip.SetTip(h, "Click to select, drag to reorder");
        bool pressed = false, collapseOnRelease = false;
        var pressAt = new Avalonia.Point();
        h.PointerPressed += (_, e) =>
        {
            // A plain press inside a bigger selection keeps the set so a
            // multi-row drag can start; if no drag follows, the release
            // below collapses to just this row, like a file explorer.
            collapseOnRelease = e.KeyModifiers == KeyModifiers.None
                && _selectedRows.Contains(b.Row) && _selectedRows.Count > 1;
            SelectFromClick(b.Row, e.KeyModifiers);
            pressed = true;
            pressAt = e.GetPosition(this);
            h.Focus();
            e.Handled = true; // the click-away clear below must not see this press
        };
        h.PointerReleased += (_, _) =>
        {
            if (pressed && collapseOnRelease)
            {
                _selectedRows.Clear(); _selectedRows.Add(b.Row); _selAnchor = b.Row;
                RepaintSelection();
            }
            pressed = false;
        };
        h.PointerMoved += (_, e) =>
        {
            // Only a real movement starts a drag, so a plain click stays a click.
            var d = e.GetPosition(this) - pressAt;
            if (!pressed || Math.Abs(d.X) + Math.Abs(d.Y) < 6) return;
            pressed = false;
            var data = new DataObject();
            // The whole selection travels; the press above guaranteed the
            // pressed row is in it.
            data.Set(RowDragFormat, _selectedRows.OrderBy(r => r).ToArray());
            _ = DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        };
        h.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space) return;
            if (!_selectedRows.Remove(b.Row)) { _selectedRows.Add(b.Row); _selAnchor = b.Row; }
            RepaintSelection();
            e.Handled = true;
        };
        return h;
    }

    void WireRowDrop(Panel p, Binding b)
    {
        DragDrop.SetAllowDrop(p, true);
        p.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(RowDragFormat) ? DragDropEffects.Move : DragDropEffects.None);
        p.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        { if (e.Data.Contains(RowDragFormat)) BindBrush(p, Panel.BackgroundProperty, "NewRowTint"); });
        p.AddHandler(DragDrop.DragLeaveEvent, (_, _) => PaintRow(b.Row)); // restore the selection tint
        p.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            PaintRow(b.Row);
            if (e.Data.Get(RowDragFormat) is not int[] srcs || srcs.Contains(b.Row)) return;
            var off = GridScroll.Offset;
            _selectedRows.Clear(); _selAnchor = -1; // rows renumber under a stale selection
            _file!.MoveRows(srcs, b.Row);
            if (DeviceContainer.IsVisible) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
            else
            {
                RebuildRows();
                RestoreListScroll(off, () => { });
            }
        });
    }

    Control HeaderRow()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(RowNumberHeaderSpacer());
        p.Children.Add(Swatch("Output (game button)", 220, OutputTint));
        p.Children.Add(Swatch("Function (behavior)", 180, FunctionTint));
        p.Children.Add(Swatch("Inputs (sips, puffs, joystick)", 240, InputTint));
        return p;
    }

    Control PrefsHeaderRow()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(RowNumberHeaderSpacer());
        p.Children.Add(Swatch("Setting", 300, OutputTint));
        p.Children.Add(Swatch("Value", 160, FunctionTint));
        p.Children.Add(Swatch("Units", 100, InputTint));
        p.Children.Add(Swatch("Description", 240, InputTint));
        return p;
    }

    Control PrefsRow(Binding b, int number)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(DragHandle(b, number));
        WireRowDrop(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);
        p.Children.Add(SuggestBox(b.Row, 0, b.Output, 300, NoSuggestions, $"Setting name for row {b.Row}", OutputTint));
        p.Children.Add(SuggestBox(b.Row, 1, b.Function, 160, NoSuggestions, $"Setting value for row {b.Row}", FunctionTint));
        // The official sheet annotates each preference with Units (column C)
        // and a Description (column D). The device ignores both, but hiding
        // them here hid the tester's own notes about what each setting does.
        p.Children.Add(SuggestBox(b.Row, 2, _file!.GetCell(b.Row, 2), 100, NoSuggestions, $"Units for row {b.Row}", InputTint));
        var desc = NoteBox(b.Row, 3, $"Description for row {b.Row}. Saved in the file, ignored by the QuadStick");
        desc.Width = 240;
        p.Children.Add(desc);
        var del = new Button { Classes = { "icon", "danger" }, Content = Glyph("IconDelete", "Error") };
        ToolTip.SetTip(del, "Delete this whole row");
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => DeleteListRow(b);
        p.Children.Add(del);
        return p;
    }

    Control BindingRow(Binding b, int number)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        p.Children.Add(DragHandle(b, number));
        WireRowDrop(p, b);
        _rowPanels[b.Row] = p;
        PaintRow(b.Row);
        // Inputs stack DOWN (below), so every other cell centers vertically
        // against the taller stack instead of stretching or hugging the top.
        Control Mid(Control c) { c.VerticalAlignment = VerticalAlignment.Center; return c; }
        p.Children.Add(Mid(SuggestBox(b.Row, 0, b.Output, 220, OutputSuggestionsFor(CurrentSheet!), $"Output for row {b.Row}", OutputTint)));
        p.Children.Add(Mid(SuggestBox(b.Row, 1, b.Function, 180, FunctionSuggestions, $"Function for row {b.Row}", FunctionTint)));

        // Extra inputs go UNDER the first one. Sideways growth forced a
        // horizontal scroll, which the tester called out as inaccessible.
        var inputsBox = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        int inputCount = Math.Max(1, b.Inputs.Count);
        for (int i = 0; i < inputCount; i++)
        {
            // Write to each input's REAL column (inputs may have gaps in C..J).
            int col = i < b.InputCols.Count ? b.InputCols[i] : FirstFreeInputColumn(b);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(SuggestBox(b.Row, col, i < b.Inputs.Count ? b.Inputs[i] : "", 240,
                InputSuggestions, $"Input {i + 1} for row {b.Row}", InputTint));
            // A round remove control beside each real input, so any input
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
                line.Children.Add(Mid(rmv));
            }
            inputsBox.Children.Add(line);
        }
        p.Children.Add(inputsBox);

        // One input: plus and trash side by side. More than one: they stack
        // into one column so the per-input trashes never push the note and
        // chevrons further right.
        var rowButtons = new StackPanel
        {
            Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Orientation = b.Inputs.Count > 1 ? Orientation.Vertical : Orientation.Horizontal,
        };
        if (inputCount < 8)
        {
            var addInput = IconButton("IconAdd", $"Add another input to row {b.Row}");
            ToolTip.SetTip(addInput, "Add another input");
            int nextCol = FirstFreeInputColumn(b);
            addInput.Click += (_, _) =>
            {
                // Add the box directly; the file only changes when a value is committed.
                var newBox = SuggestBox(b.Row, nextCol, "", 240, InputSuggestions,
                    $"Input {nextCol - 1} for row {b.Row}", InputTint);
                // Fixed width + default Stretch centers it against the wider
                // committed lines (box + trash); pin it to the same left edge.
                newBox.HorizontalAlignment = HorizontalAlignment.Left;
                inputsBox.Children.Add(newBox);
                nextCol++;
                while (nextCol < 10 && b.InputCols.Contains(nextCol)) nextCol++; // skip occupied cells
                if (nextCol >= 10) addInput.IsVisible = false;
                ((newBox as Border)!.Child as AutoCompleteBox)!.Focus();
            };
            rowButtons.Children.Add(addInput);
        }

        // The whole-row delete: a red trash circle under the plus.
        var del = new Button { Classes = { "icon", "danger" }, Content = Glyph("IconDelete", "Error") };
        ToolTip.SetTip(del, "Delete this whole row");
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => DeleteListRow(b);
        rowButtons.Children.Add(del);
        p.Children.Add(rowButtons);

        var note = NoteBox(b.Row, NoteColumn, $"Note for row {b.Row}. Saved in the file, ignored by the QuadStick");
        // p is a horizontal StackPanel with unbounded width, so without a
        // fixed Width the wrapped note would just grow sideways forever
        // instead of wrapping. The fixed width is what lets it wrap and
        // grow the row taller instead.
        note.Width = 200;
        p.Children.Add(Mid(note));

        // Reorder within the mode. Both buttons always render (disabled at the
        // edges) so the click targets stay put while a row is walked up or down.
        int rowIndex = CurrentSheet!.Bindings.IndexOf(b);
        foreach (var (delta, word, angle) in new[] { (-1, "up", 180.0), (+1, "down", 0.0) })
        {
            var move = IconButton("IconChevron", $"Move row {b.Row} {word}");
            // The chevron points right; +90 turns it down, 180+90 turns it up.
            ((PathIcon)move.Content!).RenderTransform = new RotateTransform(angle + 90);
            move.Tag = (word, b.Row);
            move.IsEnabled = rowIndex + delta >= 0 && rowIndex + delta < CurrentSheet!.Bindings.Count;
            move.Click += (_, _) => MoveListRow(b, delta);
            p.Children.Add(Mid(move));
        }
        return p;
    }

    // Swap this row with its neighbor in the same mode's binding list. After
    // the rebuild, focus follows the same-direction move button on the moved
    // row so a keyboard user can keep walking it without re-tabbing; at the
    // edge (button disabled) focus lands on the row's output cell instead.
    void MoveListRow(Binding b, int delta)
    {
        var sheet = CurrentSheet;
        if (sheet is null || _file is null) return;
        int i = sheet.Bindings.IndexOf(b);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= sheet.Bindings.Count) return;
        int destRow = sheet.Bindings[j].Row;
        var off = GridScroll.Offset;
        _selectedRows.Clear(); _selAnchor = -1; // rows renumber under a stale selection
        _file.SwapRows(b.Row, destRow);
        RebuildRows();
        RestoreListScroll(off, () =>
        {
            var key = (delta < 0 ? "up" : "down", destRow);
            var moveButton = RowsPanel.Children.OfType<StackPanel>()
                .SelectMany(row => row.Children.OfType<Button>())
                .FirstOrDefault(x => x.Tag is ValueTuple<string, int> t && t.Equals(key));
            if (moveButton is { IsEnabled: true })
            { moveButton.BringIntoView(); moveButton.Focus(); }
            else if (_cellBorders.TryGetValue($"A{destRow}", out var border))
            { border.BringIntoView(); (border.Child as AutoCompleteBox)?.Focus(); }
        });
    }

    // Delete a List View row without the scroll jumping to the top: restore the
    // saved scroll offset after the rebuild, then focus the row that slid into
    // place. (RebuildRows() clears and re-adds every row, which otherwise resets
    // the ScrollViewer to 0.)
    void DeleteListRow(Binding b)
    {
        int deletedIndex = CurrentSheet!.Bindings.IndexOf(b);
        var off = GridScroll.Offset;
        _selectedRows.Clear(); _selAnchor = -1; // rows renumber under a stale selection
        _file!.DeleteRow(b.Row);
        RebuildRows();
        RestoreListScroll(off, () => FocusRowSibling(deletedIndex));
    }

    // Scroll and focus must wait until the rebuilt panel has been measured,
    // or BringIntoView runs against a zero-size control and the ScrollViewer
    // snaps back to the top.
    static void AfterLayout(Action act) => Dispatcher.UIThread.Post(act, DispatcherPriority.Loaded);

    void RestoreListScroll(Vector offset, Action thenFocus) =>
        Dispatcher.UIThread.Post(() =>
        {
            var maxY = Math.Max(0, GridScroll.Extent.Height - GridScroll.Viewport.Height);
            GridScroll.Offset = new Vector(offset.X, Math.Min(offset.Y, maxY));
            thenFocus();
        }, DispatcherPriority.Loaded);

    Control SuggestBox(int row, int col, string value, double width, List<string> suggestions, string accessibleName, string tintKey)
    {
        var box = new AutoCompleteBox
        {
            Text = value,
            ItemsSource = suggestions,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 0,
            Width = width,
        };
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
            var old = _file.GetCell(row, col);
            if (v == old) return; // no no-op undo snapshots
            _file.SetCell(row, col, v);
            RefreshIssues();
            // Keep the device diagram's summaries current without stealing
            // focus from the detail panel the user is typing in.
            if (DeviceContainer.IsVisible) { BuildDeviceView(); return; }
            // An input appearing or disappearing changes the row's own
            // controls (its remove buttons, the + input button), so rebuild
            // the rows instead of leaving stale controls until the next view
            // switch. Deferred: rebuilding synchronously destroys the box
            // that is mid focus/key event and fights its closing dropdown,
            // the same trap TokenField defers around. Only refocus when the
            // box was focused (Enter commit); a click-away commit must not
            // steal the click's focus back.
            if (col is >= 2 and < 10 && (old.Length == 0) != (v.Length == 0))
            {
                bool refocus = box.IsFocused;
                box.IsDropDownOpen = false;
                var off = GridScroll.Offset;
                Dispatcher.UIThread.Post(() =>
                {
                    RebuildRows();
                    RestoreListScroll(off, () =>
                    {
                        if (refocus && _cellBorders.TryGetValue($"{(char)('A' + col)}{row}", out var border))
                            (border.Child as Control)?.Focus();
                    });
                });
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var wrapper = new Border
        {
            Child = box,
            // Match the thickness RefreshIssues sets on an errored cell, so
            // flagging a problem only recolors the border and never reflows the
            // row. A thinner clean border would shift the row a pixel and knock
            // the row number off center.
            BorderThickness = new Avalonia.Thickness(3),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"{(char)('A' + col)}{row}"] = wrapper;
        return wrapper;
    }

    // Column K is the first cell the device ignores, so notes live there.
    const int NoteColumn = 10;

    Control NoteBox(int row, int col, string accessibleName)
    {
        var box = new TextBox
        {
            Text = _file!.GetCell(row, col),
            Watermark = "note",
            FontSize = Size("SmallSize"),
            // A long note used to sit on one clipped line. Wrapping needs a
            // width bound to grow vertically instead of sideways, so every
            // call site must give this box a fixed Width. Enter still commits
            // (see KeyDown below), so it must not also insert a newline.
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            // Cap how tall one note can push its row before it scrolls
            // internally, so a pathological note can't fill the screen.
            MaxHeight = 92,
        };
        // TextBox doesn't expose vertical scrollbar visibility as its own
        // property; it reads the attached ScrollViewer property from its
        // own template instead.
        box.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, accessibleName);
        void Commit()
        {
            if (_file is null) return;
            var v = (box.Text ?? "").Trim();
            if (v == _file.GetCell(row, col)) return;
            _file.SetCell(row, col, v);
            RefreshIssues(); // the 1023-byte row limit can trip on a long note
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return box;
    }

    void AddRow()
    {
        if (_file is null || CurrentSheet is null) { Status("Open or create a profile first."); return; }
        int newRow = _file.AddBindingRow(CurrentSheet); // already reparses
        if (DeviceContainer.IsVisible)
        {
            _selectedZone = "unset"; // the new row has no input yet; take the user to it
            BuildDeviceView(); BuildZoneDetail(); RefreshIssues();
            return;
        }
        RebuildRows();
        // Take the user to the row they just created.
        AfterLayout(() =>
        {
            if (!_cellBorders.TryGetValue($"A{newRow}", out var border)) return;

            border.BringIntoView();
            (border.Child as AutoCompleteBox)?.Focus();

            // BringIntoView alone is not reliable here: ZoomHost scales the whole
            // tree with a LayoutTransform, and at some zoom levels the request
            // resolves against stale bounds and leaves GridScroll short of the
            // row. Compute the row's own position in GridScroll's coordinate
            // space (untouched by the ancestor zoom, same units as Offset) and
            // make sure its bottom edge is inside the viewport too.
            if (border.Parent is Control row)
            {
                ScrollRowIntoView(row);
                // A focused AutoCompleteBox is invisible to a mouse-only user
                // with no cursor to find it, so also flash the row itself.
                FlashNewRow(row);
            }
        });
    }

    // Clamped the same way RestoreListScroll clamps a restored offset: never
    // past the scrollable extent.
    void ScrollRowIntoView(Control row)
    {
        var bottom = row.TranslatePoint(new Point(0, row.Bounds.Height), RowsPanel);
        if (bottom is not { } p) return;
        var viewport = GridScroll.Viewport.Height;
        var maxY = Math.Max(0, GridScroll.Extent.Height - viewport);
        var targetY = Math.Clamp(p.Y - viewport, 0, maxY);
        if (targetY > GridScroll.Offset.Y)
            GridScroll.Offset = new Vector(GridScroll.Offset.X, targetY);
    }

    // Briefly tints a just-added row so a mouse-only user can see where it
    // landed, then clears it. The row reference is only ever touched inside
    // this closure, so if the list gets rebuilt (another edit) before the
    // timer fires, this just paints/clears a control nobody looks at anymore
    // rather than throwing.
    static void FlashNewRow(Control row)
    {
        BindBrush(row, Panel.BackgroundProperty, "NewRowTint");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            row.ClearValue(Panel.BackgroundProperty);
        };
        timer.Start();
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
            // Keep the same thickness an errored cell gets below, so toggling a
            // problem only recolors the border and never reflows the row height.
            b.BorderThickness = new Avalonia.Thickness(3);
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
                .Select(IssueItem)
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

    // One row in the problems list. Unknown-input errors get a one-click cure:
    // old profiles keep notes in the input columns (C..J), which the device
    // reads as inputs; moving the text to the notes column keeps the note and
    // clears the error.
    Control IssueItem(Issue i)
    {
        var tb = new TextBlock
        {
            Text = i.ToString(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = Size("SmallSize"),
            Classes = { i.Severity == Severity.Error ? "error" : "warn" },
            Tag = i, // lets SelectionChanged/Fix-first find the cell to focus
        };
        if (i.Kind != IssueKind.UnknownInput) return tb;

        var fix = new Button
        {
            Content = "Move to notes", Classes = { "quiet" },
            Margin = new Avalonia.Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(fix, $"Move the text in cell {i.Cell} to the notes column");
        fix.Click += (_, _) => MoveIssueTextToNotes(i);
        return new StackPanel { Children = { tb, fix }, Tag = i };
    }

    void MoveIssueTextToNotes(Issue i)
    {
        if (_file is null || !int.TryParse(i.Cell.AsSpan(1), out int row)) return;
        _file.MoveInputToNotes(row, i.Cell[0] - 'A');
        if (_deviceView) { BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); }
        else { var off = GridScroll.Offset; RebuildRows(); RestoreListScroll(off, () => { }); }
        Status($"Moved the text from {i.Cell} into the notes column.", StatusKind.Info);
    }

    // ---- Small shared UI builders for the redesigned editor ----

    // A compact aligned row: a short muted label in a fixed-width first column,
    // the field filling the rest. Collapses the old label-above-field pairs so a
    // mapping reads across in far less vertical space.
    static Control Labeled(string label, Control field)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(new TextBlock
        {
            Text = label, FontSize = Size("SmallSize"), FontWeight = FontWeight.SemiBold, Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 76, Margin = new Avalonia.Thickness(0, 0, 10, 0),
        });
        Grid.SetColumn(field, 1);
        g.Children.Add(field);
        return g;
    }

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
        var current = (b.Function ?? "").Trim();
        var tokens = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstToken = tokens.FirstOrDefault() ?? "";
        bool known = Vocab.FunctionArity.ContainsKey(firstToken);
        var currentParams = known ? string.Join(' ', tokens.Skip(1)) : "";

        var items = new List<string>(FunctionSuggestions);
        // An unknown value (e.g. a typo or a form the list doesn't carry) stays
        // selectable exactly as before; a known value with parameters shows the
        // bare name in the list and edits its values in the box below.
        if (!known && current.Length > 0 && !items.Contains(current)) items.Insert(0, current);

        var combo = new ComboBox { ItemsSource = items, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectedItem = known ? firstToken : items.FirstOrDefault(x => x == current);
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

        var paramsBox = new TextBox
        {
            Text = currentParams,
            Watermark = "optional values, e.g. 1000",
            FontSize = Size("SmallSize"),
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
            IsVisible = Vocab.FunctionArity.TryGetValue(firstToken, out var startArity) && startArity.Max > 0,
        };
        AutomationProperties.SetName(paramsBox,
            $"Optional parameter values for {firstToken}. Whole numbers separated by spaces, for example 1000");

        void Commit()
        {
            if (_file is null || combo.SelectedItem is not string name) return;
            // The raw exotic value has no arity: commit it as-is, never append params.
            if (!Vocab.FunctionArity.TryGetValue(name, out var arity))
            {
                if (name != _file.GetCell(b.Row, 1)) { _file.SetCell(b.Row, 1, name); RebuildDeviceAfterEdit(b.Row, 1); }
                return;
            }
            var p = (paramsBox.Text ?? "").Trim();
            var value = p.Length > 0 && arity.Max > 0 ? $"{name} {p}" : name;
            // Equality guard: RebuildDeviceAfterEdit replaces these controls, so a
            // stale LostFocus firing afterward must land as a no-op, not a loop.
            if (value != _file.GetCell(b.Row, 1)) { _file.SetCell(b.Row, 1, value); RebuildDeviceAfterEdit(b.Row, 1); }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string name)
            {
                bool hasParams = Vocab.FunctionArity.TryGetValue(name, out var ar) && ar.Max > 0;
                paramsBox.IsVisible = hasParams;
                AutomationProperties.SetName(paramsBox,
                    $"Optional parameter values for {name}. Whole numbers separated by spaces, for example 1000");
                if (!hasParams) paramsBox.Text = "";
            }
            Commit();
        };
        paramsBox.LostFocus += (_, _) => Commit();
        paramsBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };

        // Register the function cell like the input/output fields so a function
        // error (bad name, too many params) can be highlighted and focused here
        // too — without the wrapper, B{row} lives nowhere in _cellBorders.
        // RefreshIssues mirrors the wrapper child's accessible name onto the
        // highlight; the panel needs the combo's name or an error reads as nothing.
        var stack = new StackPanel { Children = { combo, paramsBox } };
        AutomationProperties.SetName(stack, AutomationProperties.GetName(combo));
        var wrapper = new Border
        {
            Child = stack,
            BorderThickness = new Avalonia.Thickness(2),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new Avalonia.CornerRadius(5),
        };
        _cellBorders[$"B{b.Row}"] = wrapper;
        return wrapper;
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
                // Swapping the child synchronously here fights the ComboBox as it
                // closes its own popup, so the text box never appears. Defer it.
                if (ReferenceEquals(s, TypeYourOwn)) { Dispatcher.UIThread.Post(ShowTyping); return; }
                if (s == _file.GetCell(row, col)) return;
                _file.SetCell(row, col, s);
                RebuildDeviceAfterEdit(row, col);
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
                if (v != _file.GetCell(row, col)) _file.SetCell(row, col, v);
                RebuildDeviceAfterEdit(row, col);
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
        var content = new StackPanel
        {
            Spacing = 8, MaxWidth = 340, Margin = new Avalonia.Thickness(4),
            Focusable = true, // focus lands here so screen readers read the tip, not silence
        };
        content.Children.Add(new TextBlock
        { Text = title, FontWeight = FontWeight.Bold, FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        { Text = body, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        AutomationProperties.SetName(content, $"{title}. {body}. Press Escape to close.");
        AutomationProperties.SetLiveSetting(content, AutomationLiveSetting.Polite);
        var flyout = new Flyout { Content = content, Placement = PlacementMode.Bottom };
        flyout.Opened += (_, _) => content.Focus();
        flyout.ShowAt(anchor);
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
        // The visible label carries the live count ("2 errors"); mirror it to the
        // screen-reader name so it never reads a stale "show or hide" while the
        // eye sees a number. The glyph+text content itself isn't announced.
        AutomationProperties.SetName(ProblemsToggle,
            $"{label}. {(_problemsExpanded ? "Hides" : "Shows")} the list of problems.");
        FixFirstButton.IsVisible = _problemsExpanded && errors > 0;
        ProblemsDock.IsVisible = _problemsExpanded || errors + warns > 0;
    }

    // Plain words for what a Function does, keyed on its first token so
    // "repeat 5 2000" still explains. Empty when blank or unknown, so the
    // note only appears once a behavior is actually chosen.
    static string FunctionExplain(string function)
    {
        var name = (function ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return name switch
        {
            "normal" => "Held down for as long as your input is active.",
            "toggle" => "One activation latches it on, the next releases it.",
            "repeat" => "Rapid-fire taps while your input is held.",
            "pulse" => "One short press each time you activate.",
            "delayed_latch" => "A short activation taps it; a long one latches it on.",
            "delay_on" => "Waits a moment after you activate, then presses.",
            "delay_off" => "Keeps pressing for a moment after you release.",
            "tap" => "A quick press sends one output; holding longer sends a different one.",
            "force_off" => "Turns off an output that toggle or delayed_latch left on.",
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
            // Complex button content is invisible to screen readers without
            // an explicit name; this is a safety-relevant choice.
            AutomationProperties.SetName(btn, $"Install to the QuadStick named {name}, at {root}");
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
