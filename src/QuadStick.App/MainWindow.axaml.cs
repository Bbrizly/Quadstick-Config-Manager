using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using QuadStick.Format;

namespace QuadStick.App;

public partial class MainWindow : Window
{
    // Bind any brush property to a theme token so it repaints on theme change.
    // Never resolve+assign a concrete brush for a themed color: that freezes it.
    static void BindBrush(Control target, AvaloniaProperty property, string tokenKey) =>
        target[!property] = new DynamicResourceExtension(tokenKey + "Brush");

    ProfileFile? _file;
    string? _savePath;          // where Save writes; null until saved or opened from a path
    int _sheetIndex;
    bool _deviceView = true;    // the visual mapper is the default face of the editor
    string? _selectedZone;
    QsModel _model;

    enum QsModel { FPS, Original, Singleton }
    static readonly string[] ModelNames = { "QuadStick FPS", "QuadStick Original", "QuadStick Singleton" };

    static QsModel LoadModel() =>
        Enum.TryParse<QsModel>(Settings.Load().model, out var m) ? m : QsModel.FPS;

    void SaveModel() => Settings.Save(null, model: _model.ToString(), theme: null);

    readonly Dictionary<string, Border> _cellBorders = new();
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

        HomeNewButton.Click += (_, _) => NewFromTemplate();
        HomeOpenButton.Click += async (_, _) => await OpenAsync();
        HomeHelpButton.Click += (_, _) => ShowHelp();
        ImportButton.Click += async (_, _) => await ImportAsync();

        HomeButton.Click += async (_, _) => { if (await ConfirmLeaveAsync()) ShowHome(); };
        Closing += async (_, e) =>
        {
            if (_file is not { Dirty: true } || _closeConfirmed) return;
            e.Cancel = true;
            if (await ConfirmLeaveAsync()) { _closeConfirmed = true; Close(); }
        };
        FileNameBox.LostFocus += (_, _) => CommitFileName();
        FileNameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitFileName(); };
        SaveButton.Click += async (_, _) => await SaveAsync();
        UndoButton.Click += (_, _) => UndoEdit();
        InstallButton.Click += async (_, _) => await InstallAsync();
        HelpButton.Click += (_, _) => ShowHelp();
        AddRowButton.Click += (_, _) => AddRow();
        SheetPicker.SelectionChanged += (_, _) =>
        {
            if (SheetPicker.SelectedIndex >= 0 && _file != null)
            { _sheetIndex = SheetPicker.SelectedIndex; _selectedZone = null; RefreshEditor(); }
        };

        // Selecting a problem copies it, so users can paste it into a bug
        // report or a forum post without retyping.
        IssuesList.SelectionChanged += async (_, _) =>
        {
            if (IssuesList.SelectedItem is TextBlock { Text.Length: > 0 } tb && Clipboard is { } cb)
            {
                await cb.SetTextAsync(tb.Text);
                Status("Problem copied to the clipboard.", Brushes.Green);
                IssuesList.SelectedIndex = -1; // allow copying the same one again
            }
        };

        DeviceViewButton.Click += (_, _) => { _deviceView = true; RefreshEditor(); };
        ListViewButton.Click += (_, _) => { _deviceView = false; RefreshEditor(); };
        _model = LoadModel();
        ModelPicker.ItemsSource = ModelNames;
        ModelPicker.SelectedIndex = (int)_model;
        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (ModelPicker.SelectedIndex < 0) return;
            _model = (QsModel)ModelPicker.SelectedIndex;
            SaveModel();
            if (_deviceView) { _selectedZone = null; RefreshEditor(); }
        };

        var (_, savedTheme) = Settings.Load();
        AppearancePicker.ItemsSource = new[] { "System", "Light", "Dark" };
        AppearancePicker.SelectedIndex = savedTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        AppearancePicker.SelectionChanged += (_, _) =>
        {
            var choice = (string)AppearancePicker.SelectedItem!;
            QuadStick.App.Theme.Apply(choice);
            Settings.Save(null, model: null, theme: choice);
        };

        // Ctrl (Windows/Linux) or Cmd (macOS) shortcuts.
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
            switch (e.Key)
            {
                case Key.O: _ = OpenAsync(); e.Handled = true; break;
                case Key.S: _ = SaveAsync(); e.Handled = true; break;
                case Key.N: NewFromTemplate(); e.Handled = true; break; // uses DefaultNewName
                case Key.Z: UndoEdit(); e.Handled = true; break;
            }
        };

        ShowHome();
    }

    void ShowHome()
    {
        HomeView.IsVisible = true;
        EditorView.IsVisible = false;
        RefreshHomeCards();
    }

    void ShowEditor()
    {
        HomeView.IsVisible = false;
        EditorView.IsVisible = true;
    }

    void RefreshHomeCards()
    {
        LibraryCards.Children.Clear();
        var libraryFiles = Directory.Exists(LibraryDir)
            ? Directory.GetFiles(LibraryDir, "*.csv").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        LibraryEmptyText.IsVisible = libraryFiles.Length == 0;
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
                new TextBlock { Text = name, FontSize = 18, FontWeight = FontWeight.Bold },
                new TextBlock { Text = subtitle, Classes = { "cardsub" } },
            },
        };
        card.Click += (_, _) =>
        {
            try { OpenInEditor(ProfileFile.Load(File.ReadAllText(path)), path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status($"Could not open {name}: {ex.Message}", Brushes.Crimson); ShowEditor(); }
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
        Title = "QuadStick Config Manager (unofficial) — "
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
        var s = input;
        foreach (var prefix in new[] { "mp_left_center_", "mp_right_center_", "mp_left_right_", "mp_triple_", "mp_left_", "mp_center_", "mp_right_", "right_" })
            if (z.Id is not ("joystick" or "other") && s.StartsWith(prefix)) { s = s[prefix.Length..]; break; }
        if (s.EndsWith("_soft")) s = "soft " + s[..^5];
        var extra = b.Inputs.Count > 1 ? $" +{b.Inputs.Count - 1}" : "";
        return s.Replace('_', ' ') + extra;
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
        DeviceViewButton.Classes.Set("primary", device);
        ListViewButton.Classes.Set("primary", !device);
        var connected = Device.FindCandidates().Count > 0;
        DeviceStatusText.Text = connected ? "● QuadStick connected" : "○ no QuadStick detected";
        DeviceStatusText.Foreground = connected ? Brushes.Green : Brushes.Gray;
        if (device) { BuildDeviceView(); BuildZoneDetail(); }
        else RebuildRows();
        RefreshIssues();
    }

    void BuildDeviceView()
    {
        DeviceCanvas.Children.Clear();
        var byZone = BindingsByZone();
        var visible = VisibleZones(byZone).ToList();

        DeviceCanvas.Children.Add(new TextBlock
        {
            Text = "Select a part of the QuadStick to see and change what it does.",
            FontSize = 15, Classes = { "muted" },
        });
        DeviceCanvas.Children.Add(new TextBlock
        {
            Text = ModelDescription,
            FontSize = 14, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        });

        var layout = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Avalonia.Thickness(0, 10) };

        // Joystick: the whole mouthpiece moves.
        layout.Children.Add(ZoneButton(AllZones[0], byZone, 146, 146, 73, 2));

        // Mouthpiece bar with its holes.
        var holes = new StackPanel
        { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var z in visible.Where(z => z.Id.StartsWith("mp_")))
            holes.Children.Add(ZoneButton(z, byZone, 112, 112, 56, 2));
        var mouthpieceBar = new Border
        {
            CornerRadius = new Avalonia.CornerRadius(26),
            Padding = new Avalonia.Thickness(16, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Mouthpiece", FontWeight = FontWeight.Bold, FontSize = 14,
                                    HorizontalAlignment = HorizontalAlignment.Center },
                    holes,
                },
            },
        };
        BindBrush(mouthpieceBar, Border.BackgroundProperty, "SurfaceSubtle");
        layout.Children.Add(mouthpieceBar);

        // Side tube + lip switch column.
        var sideCol = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var z in visible.Where(z => z.Id is "side" or "lip"))
            sideCol.Children.Add(ZoneButton(z, byZone, 140, 74, 16, 1));
        layout.Children.Add(sideCol);

        DeviceCanvas.Children.Add(layout);

        var extras = new WrapPanel();
        foreach (var z in visible.Where(z => z.Id is "combo" or "jacks" or "other" or "unset"))
        {
            var b = ZoneButton(z, byZone, 200, 92, 12, 2);
            (b as Button)!.Margin = new Avalonia.Thickness(0, 0, 12, 12);
            extras.Children.Add(b);
        }
        if (extras.Children.Count > 0) DeviceCanvas.Children.Add(extras);
    }

    // Which parts the selected model physically has. Zones the model lacks
    // still show when a profile maps them, but marked, so a profile made for
    // an FPS is never silently broken on a Singleton.
    bool ModelHasZone(string zoneId) =>
        _model != QsModel.Singleton
        || zoneId is not ("mp_left" or "mp_right" or "combo" or "side" or "lip" or "jacks");

    Control ZoneButton(Zone z, Dictionary<string, List<Binding>> byZone, double w, double h, double radius, int maxLines)
    {
        byZone.TryGetValue(z.Id, out var bindings);
        bool foreign = !ModelHasZone(z.Id);
        var lines = (bindings ?? new List<Binding>())
            .Take(maxLines).Select(b => $"{ShortInput(z, b)} → {b.Output}").ToList();
        var summary = bindings is null or { Count: 0 }
            ? "not mapped yet"
            : string.Join("\n", lines) + (bindings.Count > maxLines ? $"\n+{bindings.Count - maxLines} more" : "");
        if (foreign) summary = "not on your model\n" + summary;

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        { Text = z.Display, FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
        var summaryText = new TextBlock
        {
            Text = summary, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, MaxWidth = w - 20,
        };
        if (bindings is null or { Count: 0 }) summaryText.Classes.Add("muted");
        content.Children.Add(summaryText);

        var btn = new Button
        {
            Classes = { "zone" }, Width = w, Height = h,
            CornerRadius = new Avalonia.CornerRadius(radius),
            Content = content,
        };
        if (_selectedZone == z.Id) btn.Classes.Add("zoneSelected");
        if (foreign) BindBrush(btn, TemplatedControl.BorderBrushProperty, "Warning");
        var spoken = bindings is null or { Count: 0 }
            ? "nothing mapped yet"
            : string.Join(", ", bindings.Take(4).Select(b => $"{ShortInput(z, b)} presses {b.Output}"));
        if (foreign) spoken = $"Warning: your {ModelNames[(int)_model]} does not have this part, but this profile maps it. {spoken}";
        AutomationProperties.SetName(btn, $"{z.Title}. {spoken}. Press Enter to view and edit.");
        btn.Click += (_, _) => { _selectedZone = z.Id; BuildDeviceView(); BuildZoneDetail(); };
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
                FontSize = 14, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var byZone = BindingsByZone();
        byZone.TryGetValue(zone.Id, out var bindings);

        ZoneDetailPanel.Children.Add(new TextBlock
        {
            Text = $"{zone.Title}  ·  {bindings?.Count ?? 0} mapping(s)",
            FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        ZoneDetailPanel.Children.Add(new TextBlock
        { Text = zone.Blurb, FontSize = 14, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });

        if (bindings is { Count: > 0 })
        {
            var zoneInputs = Vocab.Inputs.Where(i => ZoneOf(i) == zone.Id).OrderBy(GroupRank).ThenBy(x => x).ToList();
            foreach (var b in bindings)
            {
                // One card per mapping: the input on top, what it presses below.
                var card = new StackPanel { Spacing = 6 };

                int inputCount = Math.Max(1, b.Inputs.Count);
                for (int i = 0; i < inputCount && i < 8; i++)
                {
                    var inputBox = SuggestBox(b.Row, 2 + i, i < b.Inputs.Count ? b.Inputs[i] : "", 336,
                        zoneInputs.Count > 0 ? zoneInputs : InputSuggestions,
                        $"Input {i + 1} for this {zone.Display} mapping", InputTint);
                    SetWatermark(inputBox, i == 0 ? "what you do (input)" : $"input {i + 1}");
                    card.Children.Add(inputBox);
                }

                var line2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var outBox = SuggestBox(b.Row, 0, b.Output, 168,
                    OutputSuggestionsFor(CurrentSheet!), $"Game button pressed by {ShortInput(zone, b)}", OutputTint);
                SetWatermark(outBox, "game button");
                line2.Children.Add(outBox);
                var fnBox = SuggestBox(b.Row, 1, b.Function, 114,
                    FunctionSuggestions, $"How {ShortInput(zone, b)} presses it", FunctionTint);
                SetWatermark(fnBox, "how");
                line2.Children.Add(fnBox);
                var del = new Button { Content = "X", Classes = { "danger" }, Width = 40, MinWidth = 40 };
                AutomationProperties.SetName(del, $"Remove the {ShortInput(zone, b)} mapping");
                del.Click += (_, _) => { _file!.DeleteRow(b.Row); BuildDeviceView(); BuildZoneDetail(); RefreshIssues(); };
                line2.Children.Add(del);
                card.Children.Add(line2);

                var mappingCard = new Border
                {
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(8),
                    Padding = new Avalonia.Thickness(10),
                    Child = card,
                };
                BindBrush(mappingCard, Border.BackgroundProperty, "Surface");
                BindBrush(mappingCard, Border.BorderBrushProperty, "SurfaceBorder");
                ZoneDetailPanel.Children.Add(mappingCard);
            }
        }
        else
            ZoneDetailPanel.Children.Add(new TextBlock
            { Text = "Nothing mapped here yet.", FontSize = 15, Classes = { "muted" } });

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
        Status($"Saved to {_savePath}.", Brushes.Green);
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
                Status("Imported this spreadsheet's linked tab. If the profile has more mode tabs, they are not included yet; importing every tab is coming.", Brushes.DarkOrange);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { HomeError($"Could not download the sheet: {(ex is TaskCanceledException ? "the connection timed out after 15 seconds" : ex.Message)}. Check your internet connection and the link."); }
    }

    async Task InstallAsync()
    {
        if (_file is null) { Status("Open a profile first."); return; }
        _file.Reparse();
        if (_file.HasErrors)
        { Status("Fix the errors in the Problems list before installing.", Brushes.Crimson); RefreshIssues(); return; }

        var candidates = Device.FindCandidates();
        string? root = candidates.Count switch
        {
            > 1 => await PickDeviceRootAsync(candidates),
            1 => candidates[0],
            _ => null,
        };
        if (root is null)
        {
            Status("No QuadStick drive found (a drive with default.csv on it). Pick the drive or a folder manually.");
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose the QuadStick drive" });
            if (folders.Count == 0) return;
            root = folders[0].Path.LocalPath;
        }

        if (!Device.IsInstallTarget(root))
        {
            Status("That folder does not look like a QuadStick (no default.csv at its root).", Brushes.Crimson);
            return;
        }

        try
        {
            bool confirmDefault = false;
            if (_file.Document.IsDefaultConfig)
            {
                confirmDefault = await ConfirmAsync(
                    "Overwrite default.csv?",
                    "A wrong default.csv can disable flash-drive access, and recovery needs a physical force-erase. A backup will be made first. Continue?");
                if (!confirmDefault) { Status("Install cancelled."); return; }
            }
            var file = _file;
            var installRoot = root;
            var result = await Task.Run(() => Device.Install(file, installRoot, Device.DefaultBackupDir(), confirmDefault));
            Status(result.BackupPath is null
                ? $"Installed {Path.GetFileName(result.InstalledPath)} to {root}."
                : $"Installed {Path.GetFileName(result.InstalledPath)} to {root}. Previous version backed up to {result.BackupPath}.",
                Brushes.Green);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { Status($"Install failed, device unchanged: {ex.Message}", Brushes.Crimson); }
    }

    bool _closeConfirmed;

    void CommitFileName()
    {
        if (_file is null) return;
        var v = (FileNameBox.Text ?? "").Trim();
        if (v.Length == 0 || v == _file.Document.CsvFileName) return;
        _file.SetCell(_file.Document.FileNameCellRow, 0, v);
        Title = $"QuadStick Config Manager (unofficial) — {v}";
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
        Status("Change undone.");
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
                FontSize = 15, Classes = { "muted" }, Margin = new Avalonia.Thickness(4, 12),
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
                Padding = new Avalonia.Thickness(8, 6),
                Child = new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 14 },
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
            Padding = new Avalonia.Thickness(8, 6),
            Child = new TextBlock { Text = "Setting", FontWeight = FontWeight.Bold, FontSize = 14 },
        };
        BindBrush(settingHeader, Border.BackgroundProperty, OutputTint);
        p.Children.Add(settingHeader);
        var valueHeader = new Border
        {
            Width = 160, CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(8, 6),
            Child = new TextBlock { Text = "Value", FontWeight = FontWeight.Bold, FontSize = 14 },
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
        del.Click += (_, _) => { _file!.DeleteRow(b.Row); RebuildRows(); };
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
            p.Children.Add(SuggestBox(b.Row, 2 + i, i < b.Inputs.Count ? b.Inputs[i] : "", 240,
                InputSuggestions, $"Input {i + 1} for row {b.Row}", InputTint));

        var del = new Button { Content = "Delete row", Classes = { "danger" } };
        AutomationProperties.SetName(del, $"Delete row {b.Row}");
        del.Click += (_, _) => { _file!.DeleteRow(b.Row); RebuildRows(); };

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

        p.Children.Add(del);
        return p;
    }

    Control SuggestBox(int row, int col, string value, double width, List<string> suggestions, string accessibleName, string tintKey)
    {
        var box = new AutoCompleteBox
        {
            Text = value,
            Width = width,
            ItemsSource = suggestions,
            FilterMode = AutoCompleteFilterMode.Contains,
            MinimumPrefixLength = 1,
        };
        box[!TemplatedControl.BackgroundProperty] = new DynamicResourceExtension(tintKey + "Brush");
        AutomationProperties.SetName(box, accessibleName);
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

    void RefreshIssues()
    {
        foreach (var b in _cellBorders.Values) b.BorderBrush = Brushes.Transparent;
        if (_file is null) { IssuesList.ItemsSource = null; return; }

        IssuesList.ItemsSource = _file.Issues.Count == 0
            ? new List<Control>
              {
                  new TextBlock { Text = "No problems found.", FontSize = 14,
                                  Classes = { "success" }, Margin = new Avalonia.Thickness(4) },
              }
            : _file.Issues
                .OrderBy(i => i.Severity == Severity.Error ? 0 : 1)
                .Select(i => (Control)new TextBlock
                {
                    Text = i.ToString(),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Classes = { i.Severity == Severity.Error ? "error" : "warn" },
                })
                .ToList();

        foreach (var issue in _file.Issues)
            if (_cellBorders.TryGetValue(issue.Cell, out var border))
                BindBrush(border, Border.BorderBrushProperty, issue.Severity == Severity.Error ? "Error" : "Warning");

        var errors = _file.Issues.Count(i => i.Severity == Severity.Error);
        var warns = _file.Issues.Count - errors;
        Status(errors + warns == 0
                ? "No problems. Ready to save or install."
                : $"{errors} error(s), {warns} warning(s). Errors block installing.",
            errors > 0 ? Brushes.Crimson : warns > 0 ? Brushes.DarkOrange : Brushes.Green);
    }

    static void SetWatermark(Control suggestBoxWrapper, string text)
    {
        if ((suggestBoxWrapper as Border)?.Child is AutoCompleteBox box) box.Watermark = text;
    }

    void Status(string text, IBrush? color = null)
    {
        StatusText.Text = text;
        StatusText.Foreground = color ?? new SolidColorBrush(Color.Parse("#444444"));
    }

    void ShowHelp()
    {
        var sections = new (string Title, string Body)[]
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
             "Tab moves between fields, arrows navigate suggestion lists, Enter confirms. Ctrl/Cmd+O open, S save, N new, Z undo. Every control announces itself to screen readers."),

            ("Found a problem?",
             "Select a problem in the list to copy it. File at github.com/Bbrizly/Quadstick-Config-Manager/issues — say what you did and what went wrong."),
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(28), Spacing = 14, MaxWidth = 640 };
        panel.Children.Add(new TextBlock { Text = "Quick guide", FontSize = 24, FontWeight = FontWeight.Bold });
        foreach (var (title, body) in sections)
        {
            panel.Children.Add(new TextBlock
            { Text = title, FontSize = 17, FontWeight = FontWeight.Bold, Margin = new Avalonia.Thickness(0, 8, 0, 0) });
            panel.Children.Add(new TextBlock
            { Text = body, FontSize = 15, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        }

        var win = new Window
        {
            Title = "Quick guide",
            Width = 720, Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel },
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
                        new TextBlock { Text = name, FontWeight = FontWeight.Bold, FontSize = 15 },
                        new TextBlock { Text = root, FontSize = 14, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                    },
                },
                MinWidth = 360,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = root,
            };
            btn.Click += (_, _) => { picked = (string)btn.Tag!; dialog.Close(); };
            choices.Children.Add(btn);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 16,
            MaxWidth = 520,
            Children =
            {
                new TextBlock { Text = "Multiple QuadSticks found", FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = "Choose which drive to install to:", TextWrapping = TextWrapping.Wrap, FontSize = 15 },
                choices,
                cancel,
            },
        };
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
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 16,
                MaxWidth = 480,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } },
                },
            },
        };
        var result = false;
        yes.Click += (_, _) => { result = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }
}
