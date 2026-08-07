// src/QuadStick.App/GalleryWindow.cs
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace QuadStick.App;

// The workbench for the app's own appearance. Every button, every piece of
// text, every field and every colour token on one page, with the numbers and
// the colours editable while it runs.
//
// It exists because the look was only ever visible in situ: to judge a corner
// radius you had to find a screen that used it, and to judge a colour you had
// to find a state that showed it. Half the styles were never seen side by side
// at all, so they drifted (eight different corner radii, at the last count).
//
// It is not a screen of the program. Nothing links to it and users never meet
// it. Open it with:  dotnet run --project src/QuadStick.App -- --gallery
// or  make gallery.
//
// The loop it is for: turn the knobs until it looks right, press "Copy the
// numbers", paste the block into Style.cs or Palette.cs. Nothing here writes
// to those files, because a tool that edits its own source while you are
// looking at it is a tool you cannot trust.
public class GalleryWindow : Window
{
    readonly StackPanel _specimens = new() { Spacing = 28 };
    readonly TextBox _readout = new()
    {
        AcceptsReturn = true, IsReadOnly = true, FontFamily = new FontFamily("monospace"),
        MinHeight = 120, TextWrapping = TextWrapping.NoWrap,
    };
    // Colour edits are per theme, because a palette is per theme: what you
    // type while Light is showing belongs in Palette.Light.
    readonly Dictionary<string, string> _editedLight = new();
    readonly Dictionary<string, string> _editedDark = new();
    Dictionary<string, string> Edited => IsDark ? _editedDark : _editedLight;
    IReadOnlyDictionary<string, string> Base => IsDark ? Palette.Dark : Palette.Light;
    readonly Dictionary<string, double> _editedNumbers = new();
    StackPanel _colourList = new();

    bool IsDark => Application.Current!.ActualThemeVariant == ThemeVariant.Dark;

    public GalleryWindow()
    {
        Title = "Appearance gallery";
        Width = 1280;
        Height = 900;

        BuildSpecimens();
        var body = new ScrollViewer
        {
            Padding = new Thickness(24),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _specimens,
        };

        var panel = new DockPanel { LastChildFill = true };
        var side = Knobs();
        DockPanel.SetDock(side, Dock.Left);
        panel.Children.Add(side);
        panel.Children.Add(body);
        Content = panel;
        Refresh();
    }

    // ---- the knobs ----

    Control Knobs()
    {
        var stack = new StackPanel { Spacing = 14, Width = 330 };

        var theme = new ComboBox
        {
            ItemsSource = new[] { "Light", "Dark" },
            SelectedIndex = IsDark ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(theme, "Which theme the gallery is showing");
        theme.SelectionChanged += (_, _) =>
        {
            QuadStick.App.Theme.Apply(theme.SelectedIndex == 1 ? "Dark" : "Light");
            ApplyColours();   // the other theme's edits, re-applied over the new base
            BuildColourList();
            Refresh();
        };
        stack.Children.Add(Heading("Theme"));
        stack.Children.Add(theme);

        stack.Children.Add(Heading("Numbers"));
        // Not every token: the ones worth an eye. The rest still move, from
        // Style.cs, and the readout below prints whatever is current.
        foreach (var key in new[]
        {
            "BodySize", "SmallSize", "SubheadSize", "SectionSize", "TitleSize",
            "ControlRadius", "CellRadius", "TrackRadius", "IconRadius",
            "ControlHeight", "IconButton",
        })
            stack.Children.Add(Knob(key));

        stack.Children.Add(Heading("Colours"));
        stack.Children.Add(new TextBlock
        {
            Text = "Type a hex. The whole gallery follows, and the ratio beside "
                 + "it is the real contrast against this theme's Surface.",
            TextWrapping = TextWrapping.Wrap, FontSize = Size("SmallSize"), Classes = { "muted" },
        });
        _colourList = new StackPanel { Spacing = 6 };
        BuildColourList();
        stack.Children.Add(_colourList);

        stack.Children.Add(Heading("Take it away"));
        var copy = new Button { Content = "Copy the numbers", HorizontalAlignment = HorizontalAlignment.Stretch };
        copy.Click += async (_, _) =>
        {
            Print();
            if (Clipboard is { } c) await c.SetTextAsync(_readout.Text ?? "");
        };
        stack.Children.Add(copy);
        stack.Children.Add(_readout);
        Print();

        var border = new Border
        {
            Padding = new Thickness(16), BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        MainWindow.BindBrushTo(border, Border.BackgroundProperty, "SurfaceSubtle");
        MainWindow.BindBrushTo(border, Border.BorderBrushProperty, "SurfaceBorder");
        return border;
    }

    Control Knob(string key)
    {
        double start = _editedNumbers.TryGetValue(key, out var e) ? e : Style.Numbers[key];
        var value = new TextBlock { FontSize = Size("SmallSize"), Classes = { "muted" } };
        var slider = new Slider
        {
            Minimum = 0, Maximum = Math.Max(64, start * 2), Value = start,
            TickFrequency = 1, IsSnapToTickEnabled = true,
        };
        AutomationProperties.SetName(slider, $"{key}, currently {start}");
        void Show(double v)
        {
            value.Text = $"{key}  {v:0}";
            AutomationProperties.SetName(slider, $"{key}, currently {v:0}");
        }
        Show(start);
        slider.PropertyChanged += (_, e2) =>
        {
            if (e2.Property != RangeBase.ValueProperty) return;
            _editedNumbers[key] = slider.Value;
            Style.Set(key, slider.Value);
            Show(slider.Value);
            Print();
        };
        return new StackPanel { Children = { value, slider } };
    }

    void BuildColourList()
    {
        _colourList.Children.Clear();
        foreach (var key in Base.Keys)
        {
            var current = Edited.TryGetValue(key, out var e) ? e : Base[key];
            var swatch = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(Style.Numbers["CellRadius"]),
                Background = new SolidColorBrush(Color.Parse(current)),
                BorderThickness = new Thickness(1),
            };
            MainWindow.BindBrushTo(swatch, Border.BorderBrushProperty, "SurfaceBorder");
            var box = new TextBox { Text = current, Width = 96, FontSize = Size("SmallSize"), MinHeight = 34 };
            AutomationProperties.SetName(box, $"Hex colour for {key}");
            var name = new TextBlock
            {
                Text = key, FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = 110,
            };
            var ratio = new TextBlock
            {
                FontSize = Size("SmallSize"), VerticalAlignment = VerticalAlignment.Center, Width = 44,
            };
            void ShowRatio(string hex)
            {
                var surface = Edited.TryGetValue("Surface", out var s) ? s : Base["Surface"];
                try { ratio.Text = $"{Contrast.Ratio(hex, surface):0.0}"; }
                catch { ratio.Text = "?"; }
            }
            ShowRatio(current);
            box.TextChanged += (_, _) =>
            {
                var typed = (box.Text ?? "").Trim();
                if (typed.Length is not (7 or 4) || typed[0] != '#') return;
                if (!Color.TryParse(typed, out var parsed)) return;
                // A box raises TextChanged when it is first templated, so
                // without this every token would report itself as edited and
                // the readout would hand back the palette it started with.
                if (string.Equals(typed, Base[key], StringComparison.OrdinalIgnoreCase))
                    Edited.Remove(key);
                else Edited[key] = typed;
                swatch.Background = new SolidColorBrush(parsed);
                Application.Current!.Resources[key + "Brush"] = new SolidColorBrush(parsed);
                ShowRatio(typed);
                Print();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(swatch);
            row.Children.Add(name);
            row.Children.Add(box);
            row.Children.Add(ratio);
            _colourList.Children.Add(row);
        }
    }

    // Re-apply this theme's edits after a theme switch, and drop the other
    // theme's, so the swatches never show Light's blue over a Dark surface.
    void ApplyColours()
    {
        foreach (var key in Base.Keys)
        {
            var hex = Edited.TryGetValue(key, out var e) ? e : Base[key];
            Application.Current!.Resources[key + "Brush"] = new SolidColorBrush(Color.Parse(hex));
        }
    }

    void Print()
    {
        var lines = new List<string>();
        if (_editedNumbers.Count > 0)
        {
            lines.Add("// Style.cs");
            foreach (var (key, v) in _editedNumbers.OrderBy(k => k.Key))
                lines.Add($"[\"{key}\"] = {v:0},");
        }
        foreach (var (which, edits) in new[] { ("Light", _editedLight), ("Dark", _editedDark) })
        {
            if (edits.Count == 0) continue;
            lines.Add($"// Palette.{which}");
            foreach (var (key, hex) in edits.OrderBy(k => k.Key))
                lines.Add($"[\"{key}\"] = \"{hex.ToUpperInvariant()}\",");
        }
        _readout.Text = lines.Count > 0
            ? string.Join("\n", lines)
            : "Nothing changed yet. Move a slider or type a hex, and the lines to "
              + "paste into Style.cs or Palette.cs appear here.";
    }

    // A slider changes a number, and a control that reads it through a style
    // follows on its own. The specimens that hold their own numbers (the
    // swatches, the tint plates) are rebuilt by hand.
    void Refresh()
    {
        BuildSpecimens();
    }

    // ---- the specimens ----

    void BuildSpecimens()
    {
        _specimens.Children.Clear();
        _specimens.Children.Add(Section("Text", TypeSpecimens()));
        _specimens.Children.Add(Section("Buttons", ButtonSpecimens()));
        _specimens.Children.Add(Section("Fields", FieldSpecimens()));
        _specimens.Children.Add(Section("Surfaces and tints", SurfaceSpecimens()));
        _specimens.Children.Add(Section("Shape", ShapeSpecimens()));
    }

    static Control Section(string title, Control body)
    {
        var head = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } };
        var rule = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 10) };
        MainWindow.BindBrushTo(rule, Border.BackgroundProperty, "SurfaceBorder");
        return new StackPanel { Children = { head, rule, body } };
    }

    static Control Row(params Control[] items)
    {
        var wrap = new WrapPanel();
        foreach (var c in items) { c.Margin = new Thickness(0, 0, 12, 12); wrap.Children.Add(c); }
        return wrap;
    }

    static Control Labelled(string label, Control c) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            c,
            new TextBlock { Text = label, FontSize = 11, Classes = { "muted" } },
        },
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    Control TypeSpecimens()
    {
        var stack = new StackPanel { Spacing = 6 };
        foreach (var key in new[] { "TitleSize", "SectionSize", "SubheadSize", "BodySize", "SmallSize" })
            stack.Children.Add(new TextBlock
            {
                Text = $"{key}  {Size(key):0}  Map every sip and puff",
                FontSize = Size(key),
            });
        stack.Children.Add(new TextBlock { Text = "section: A PLATE LEGEND", Classes = { "section" } });
        foreach (var cls in new[] { "secondary", "muted", "success", "warn", "error", "cardsub" })
            stack.Children.Add(new TextBlock
            {
                Text = $"{cls}: the quick brown fox", FontSize = Size("BodySize"),
                Classes = { cls }, HorizontalAlignment = HorizontalAlignment.Left,
            });
        return stack;
    }

    Control ButtonSpecimens()
    {
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Row(
            Labelled("default", new Button { Content = "Save" }),
            Labelled("primary", new Button { Content = "Install to QuadStick", Classes = { "primary" } }),
            Labelled("quiet", new Button { Content = "Modes...", Classes = { "quiet" } }),
            Labelled("danger", new Button { Content = "Delete", Classes = { "danger" } }),
            Labelled("disabled", new Button { Content = "Undo", IsEnabled = false }),
            Labelled("icon", new Button { Classes = { "icon" }, Content = "?" }),
            Labelled("icon danger", new Button { Classes = { "icon", "danger" }, Content = "x" })));

        var track = new Border { Classes = { "switchtrack" } };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (word, primary) in new[] { ("Device view", true), ("Parts list", false), ("List view", false) })
            keys.Children.Add(new Button
            { Content = word, Classes = { "switchkey", primary ? "primary" : "switchkey" } });
        track.Child = keys;
        stack.Children.Add(Labelled("switchtrack + switchkey", track));

        var card = new Button { Classes = { "card" } };
        card.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "New profile", FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold },
                new TextBlock { Text = "Start from the factory default layout", Classes = { "cardsub" } },
            },
        };
        stack.Children.Add(Labelled("card", card));

        // The device diagram's parts. Selection here is an outline and a wash,
        // never a solid fill, so both states have to be seen together.
        stack.Children.Add(Row(
            Labelled("zone", new ToggleButton
            { Classes = { "zone" }, Content = "Lip", MinWidth = 120, MinHeight = 72 }),
            Labelled("zone checked", new ToggleButton
            { Classes = { "zone" }, Content = "Hard sip", IsChecked = true, MinWidth = 120, MinHeight = 72 })));

        // A toolbar is a wrap panel whose children carry their own gap, so the
        // spacing between commands is a style, not a container property.
        var toolbar = new WrapPanel { Classes = { "toolbar" } };
        foreach (var word in new[] { "Save", "Undo", "Help" })
            toolbar.Children.Add(new Button { Content = word });
        stack.Children.Add(Labelled("toolbar", toolbar));
        return stack;
    }

    Control FieldSpecimens() => Row(
        Labelled("TextBox", new TextBox { Text = "walking.csv", Width = 220 }),
        Labelled("TextBox empty", new TextBox { Watermark = "note", Width = 160 }),
        Labelled("ComboBox", new ComboBox
        { ItemsSource = new[] { "1: Walking", "2: Driving" }, SelectedIndex = 0, Width = 200 }),
        Labelled("AutoCompleteBox", new AutoCompleteBox
        { ItemsSource = new[] { "sip_threshold" }, Text = "sip_threshold", Width = 200 }),
        Labelled("CheckBox", new CheckBox { Content = "Back up to Google Sheets", IsChecked = true }),
        Labelled("NumericUpDown", new NumericUpDown { Value = 40, Width = 160 }));

    Control SurfaceSpecimens()
    {
        var wrap = new WrapPanel();
        foreach (var key in Base.Keys)
        {
            var hex = Edited.TryGetValue(key, out var e) ? e : Base[key];
            var plate = new Border
            {
                Width = 150, Height = 66, Padding = new Thickness(8),
                CornerRadius = new CornerRadius(Style.Numbers["CellRadius"]),
                Margin = new Thickness(0, 0, 10, 10),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = key, FontSize = 12, FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Readable(hex)),
                        },
                        new TextBlock
                        {
                            Text = hex.ToUpperInvariant(), FontSize = 11,
                            Foreground = new SolidColorBrush(Readable(hex)),
                        },
                    },
                },
            };
            MainWindow.BindBrushTo(plate, Border.BorderBrushProperty, "SurfaceBorder");
            wrap.Children.Add(plate);
        }
        return wrap;
    }

    // Black or white, whichever the eye can actually read on this plate. The
    // gallery would otherwise print a token's own name in a colour that
    // vanishes into it, which is the exact failure it is here to catch.
    static Color Readable(string hex) =>
        Contrast.Ratio(hex, "#000000") >= Contrast.Ratio(hex, "#FFFFFF") ? Colors.Black : Colors.White;

    Control ShapeSpecimens()
    {
        var wrap = new WrapPanel();
        foreach (var key in Style.Numbers.Keys.Where(k => k.EndsWith("Radius")))
        {
            double r = Size(key);
            var box = new Border
            {
                Width = 96, Height = 56, CornerRadius = new CornerRadius(r),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 10),
                Child = new TextBlock
                {
                    Text = $"{key} {r:0}", FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            MainWindow.BindBrushTo(box, Border.BackgroundProperty, "Surface");
            MainWindow.BindBrushTo(box, Border.BorderBrushProperty, "SurfaceBorder");
            wrap.Children.Add(box);
        }
        return wrap;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = Size("SubheadSize"), FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 8, 0, 0),
    };
}
