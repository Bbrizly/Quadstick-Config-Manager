using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace QuadStick.App;

// Every control, text style and colour token on one page, editable live.
// Not a screen of the program: nothing links to it. Open with `make gallery`.
// Turn the knobs, then paste what it prints into Style.cs or Palette.cs. It
// never writes to those files itself.
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
        Classes.Add("dialog");
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

    // Ordered by what a user touches, not by what is easy to draw. Buttons are
    // the app's whole surface, so they come first and the most pressed ones
    // come first inside them. Colour and shape are last: they are checked, not
    // worked on, once the controls are right.
    void BuildSpecimens()
    {
        _specimens.Children.Clear();
        _specimens.Children.Add(Section("Actions",
            "Press it and something happens. Nothing here holds a state.",
            ActionSpecimens()));
        _specimens.Children.Add(Section("Items",
            "Press it and something is now chosen. Every one of these has an on "
            + "state and an off state, so both are shown side by side.",
            ItemSpecimens()));
        _specimens.Children.Add(Section("Fields", "Somewhere to type or pick a value.", FieldSpecimens()));
        _specimens.Children.Add(Section("Text", "The type scale and the meaning classes.", TypeSpecimens()));
        _specimens.Children.Add(Section("Surfaces and tints", "Every colour token, named.", SurfaceSpecimens()));
        _specimens.Children.Add(Section("Shape", "Every radius, side by side.", ShapeSpecimens()));
    }

    static Control Section(string title, string subtitle, Control body)
    {
        var head = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } };
        var sub = new TextBlock
        {
            Text = subtitle, FontSize = Size("SmallSize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap, MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 12) };
        MainWindow.BindBrushTo(rule, Border.BackgroundProperty, "SurfaceBorder");
        return new StackPanel { Children = { head, sub, rule, body } };
    }

    static Control Row(params Control[] items)
    {
        var wrap = new WrapPanel();
        foreach (var c in items) { c.Margin = new Thickness(0, 0, 16, 16); wrap.Children.Add(c); }
        return wrap;
    }

    // Name and job under every specimen. Two styles that look alike in
    // isolation are told apart by what they are for, and that line is what
    // says whether a difference on screen is deliberate or drift.
    static Control Spec(string name, string job, Control c, double width = 250) => new StackPanel
    {
        Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = width,
        Children =
        {
            new StackPanel { Children = { c }, HorizontalAlignment = HorizontalAlignment.Left },
            new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeight.Bold },
            new TextBlock
            {
                Text = job, FontSize = 11, Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap,
            },
        },
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
        stack.Children.Add(new TextBlock { Text = "brandmark: QCM", Classes = { "brandmark" } });
        foreach (var cls in new[] { "secondary", "muted", "success", "warn", "error", "cardsub" })
            stack.Children.Add(new TextBlock
            {
                Text = $"{cls}: the quick brown fox", FontSize = Size("BodySize"),
                Classes = { cls }, HorizontalAlignment = HorizontalAlignment.Left,
            });
        return stack;
    }

    static Button Btn(string text, params string[] classes)
    {
        var b = new Button { Content = text };
        foreach (var c in classes) b.Classes.Add(c);
        return b;
    }

    // The same word on every kind, in one row. Each specimen below carries its
    // own real wording, which is what it needs to be judged, but it also means
    // no two are ever the same size: this row is the only place the styles can
    // be compared with nothing else changing.
    static Control SameWord()
    {
        var wrap = new WrapPanel();
        foreach (var cls in new[] { "primary", "", "quiet", "danger" })
        {
            var b = cls.Length == 0 ? Btn("Install") : Btn("Install", cls);
            b.Margin = new Thickness(0, 0, 10, 0);
            wrap.Children.Add(b);
        }
        return wrap;
    }

    Control ActionSpecimens()
    {
        var stack = new StackPanel { Spacing = 16 };
        // Wide on purpose: the comparison only works while all four are on one
        // line, so this one is not held to the specimen column width.
        stack.Children.Add(Spec("the four kinds, one word",
            "Same label on each, so the only difference left is the style.", SameWord(), 640));

        // Most pressed first. Save and the toolbar commands are the ordinary
        // button; the row icons are pressed more often than anything, once per
        // edit; primary appears once a screen and danger almost never.
        stack.Children.Add(Row(
            Spec("default", "Save, Open, and most toolbar commands. The ordinary one.",
                Btn("Save")),
            Spec("primary", "Install to QuadStick. One a screen at most: it is the thing to do next.",
                Btn("Install to QuadStick", "primary")),
            Spec("icon", "Add and delete on every editor row, so it is pressed more than any other. 40px floor.",
                Btn("+", "icon")),
            Spec("quiet", "A command that must not compete with Save, like Modes or Advanced.",
                Btn("Modes...", "quiet")),
            Spec("icon danger", "Delete one row. Red only where the thing is gone for good.",
                Btn("x", "icon", "danger")),
            Spec("danger", "Delete a profile. Rare, deliberate, never what focus lands on first.",
                Btn("Delete", "danger")),
            Spec("homeaction", "A labeled launch command on the home console; clear without relying on an icon.",
                Btn("Open a file", "homeaction")),
            Spec("disabled", "Nothing to undo yet. It stays on screen and says so, rather than vanishing.",
                new Button { Content = "Undo", IsEnabled = false })));

        // A toolbar is a wrap panel whose children carry their own gap, so the
        // spacing between commands is a style, not a container property.
        var toolbar = new WrapPanel { Classes = { "toolbar" } };
        foreach (var word in new[] { "Save", "Undo", "Help" })
            toolbar.Children.Add(Btn(word));
        stack.Children.Add(Spec("toolbar", "Holds the actions. The gap is a style on the children, not the panel.",
            toolbar));
        return stack;
    }

    // Everything here is a Button or a ToggleButton too, which is the point of
    // keeping it apart: it presses like a button and it means something else.
    // Press Save twice and it saves twice; press a zone twice and you are back
    // where you started. On and off are shown together because a state you
    // cannot compare to its opposite cannot be judged.
    Control ItemSpecimens()
    {
        var stack = new StackPanel { Spacing = 16 };

        stack.Children.Add(Row(
            Spec("card", "A profile on the home screen. The first thing pressed in a session.",
                Card("Walking", false)),
            Spec("card, pointer over it", "Only the border moves. A card is content, so it must not look armed.",
                Card("Driving", true))));

        var track = new Border { Classes = { "switchtrack" } };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (word, on) in new[] { ("Device view", true), ("Parts list", false), ("List view", false) })
            keys.Children.Add(on ? Btn(word, "switchkey", "primary") : Btn(word, "switchkey"));
        track.Child = keys;
        stack.Children.Add(Spec("switchtrack + switchkey",
            "Which editor you are in. One key is always on, and the on one is the primary style.", track));

        var brand = new Button { Classes = { "shellbrandbutton" } };
        brand.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 11,
            Children =
            {
                new Border
                {
                    Width = 38, Height = 38, CornerRadius = new CornerRadius(9),
                    Background = Avalonia.Media.Brushes.SlateGray,
                },
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "QCM", Classes = { "shellbrand" } },
                        new TextBlock { Text = "QuadStick Config Manager", Classes = { "shellcaption" } },
                    },
                },
            },
        };
        stack.Children.Add(Spec("shellbrandbutton",
            "The mark in the corner is the way home. No plate until you point at it, so it stays chrome rather than reading as another button in the nav row.",
            brand));

        // Selection is an outline and a wash, never a solid fill: a filled zone
        // reads as a button that is about to fire, and the device diagram has
        // to be readable at a glance without colour doing the work alone.
        stack.Children.Add(Row(
            Spec("zone", "A part of the device, unselected.",
                Zone("Lip", false)),
            Spec("zone checked", "The same part, selected. An outline and a wash, never a fill.",
                Zone("Hard sip", true))));
        return stack;
    }

    // The hover state is set, not drawn. A specimen that only claims to be
    // hovered would go on looking right after the style behind it changed,
    // which is the one thing the gallery exists to stop.
    static Control Card(string name, bool over)
    {
        var card = new Button { Classes = { "card" } };
        if (over) ((IPseudoClasses)card.Classes).Set(":pointerover", true);
        card.Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = name, FontSize = Size("SectionSize"), FontWeight = FontWeight.Bold },
                new TextBlock { Text = "12 bindings, 3 modes", Classes = { "cardsub" } },
            },
        };
        return card;
    }

    static Control Zone(string name, bool on) => new ToggleButton
    { Classes = { "zone" }, Content = name, IsChecked = on, MinWidth = 120, MinHeight = 72 };

    Control FieldSpecimens() => Row(
        Spec("TextBox", "A name, a note, a value. The 48px height is a target floor, not a look.",
            new TextBox { Text = "walking.csv", Width = 220 }),
        Spec("TextBox, empty", "The watermark says what belongs here without filling it in for anyone.",
            new TextBox { Watermark = "note", Width = 160 }),
        Spec("ComboBox", "A closed list, like which mode a binding is on.",
            new ComboBox { ItemsSource = new[] { "1: Walking", "2: Driving" }, SelectedIndex = 0, Width = 200 }),
        Spec("AutoCompleteBox", "An open list: the device's names, typed or picked.",
            new AutoCompleteBox { ItemsSource = new[] { "sip_threshold" }, Text = "sip_threshold", Width = 200 }),
        Spec("CheckBox", "One setting, on or off.",
            new CheckBox { Content = "Back up to Google Sheets", IsChecked = true }),
        Spec("NumericUpDown", "A number with arrows. It never rounds what somebody typed.",
            new NumericUpDown { Value = 40, Width = 160 }));

    Control SurfaceSpecimens()
    {
        var wrap = new WrapPanel();
        var nav = new Button
        {
            Classes = { "shellnav", "active" },
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new PathIcon { Data = (Geometry)Application.Current!.FindResource("IconHome")! },
                    new TextBlock { Text = "Home" },
                },
            },
        };
        wrap.Children.Add(new Border
        {
            Classes = { "appchrome" }, Width = 360, Margin = new Thickness(0, 0, 10, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "QCM", Classes = { "shellbrand" } },
                    new TextBlock { Text = "Config Manager", Classes = { "shellcaption" } },
                    nav,
                    new Button { Content = "⚙", Classes = { "shellutility" } },
                },
            },
        });
        // The editor's command band, with the two kinds of button that sit on
        // it: a plain command lifted off the fill, and the accented one.
        wrap.Children.Add(new Border
        {
            Classes = { "editorchrome" }, Width = 360, Margin = new Thickness(0, 0, 10, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new Button { Content = "Save" },
                    new Button { Content = "Install", Classes = { "primary" } },
                    new Button { Content = "Share", Classes = { "quiet" } },
                },
            },
        });
        wrap.Children.Add(new TextBlock
        {
            Text = "Your profiles", Classes = { "pagetitle" }, Width = 260,
            Margin = new Thickness(0, 0, 10, 10),
        });
        wrap.Children.Add(new Border
        {
            Classes = { "homepanel" }, Width = 220, Margin = new Thickness(0, 0, 10, 10),
            Child = new TextBlock
            {
                Text = "homepanel\nA rounded console section that groups one home-screen job.",
                TextWrapping = TextWrapping.Wrap,
            },
        });
        wrap.Children.Add(new Border
        {
            Classes = { "dialog", "dialogshell" }, Width = 220, Margin = new Thickness(0, 0, 10, 10),
            Child = new Border
            {
                Classes = { "dialogheader" },
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock { Text = "dialogshell", Classes = { "dialogtitle" } },
                        new Button { Content = "×", Classes = { "dialogclose" } },
                    },
                },
            },
        });
        wrap.Children.Add(Btn("New profile", "homeaction", "featured"));
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
