using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// The picture at the top of the Device page. It sits above the settings and
// does not scroll, so the part you are tuning stays in front of you while you
// drag the slider that changes it.
//
// Two halves. On the left the photo of the device with a ring drawn on the
// parts the open group is about, so "Sip and puff" points at the three holes
// instead of asking somebody to already know the word. On the right a pad
// showing the joystick's own two settings as circles, with a dot on it for
// where the stick actually is.
//
// The pad is the reason this is not a decoration. Centre dead zone and full
// deflection are a percent of travel each, which is unreadable as a number and
// obvious as a circle: drag the slider, watch the ring move, push the stick and
// see which ring the dot lands in.
//
// Everything the picture says is said again in the line of text under it. A
// ring is a cue somebody has to see.
public partial class MainWindow
{
    Canvas? _bandPhoto;      // the photo and the rings drawn over its parts
    Canvas? _bandPad;        // the joystick pad
    TextBlock? _bandParts;   // which parts the open group changes
    TextBlock? _bandRings;   // what the two circles on the pad mean
    TextBlock? _bandLive;    // where the stick is, or why nothing is reading it
    TextBlock? _bandPress;   // what the QuadStick is sending right now
    StackPanel? _bandPadBlock;

    // Photo hotspots the settings groups point at. A group with no entry here
    // changes something with no outside part to point at, and the photo is
    // then drawn plain rather than ringing a part at random.
    static readonly Dictionary<string, string[]> CategoryParts = new(StringComparer.Ordinal)
    {
        ["Joystick"] = new[] { "joystick" },
        ["Mouse"] = new[] { "joystick" },
        ["Sip and puff"] = new[] { "mp_left", "mp_center", "mp_right", "side" },
        ["Lip sensor"] = new[] { "lip" },
        // "leds" is the one entry with no hotspot on the photo: the lights are
        // a row across the case, so they are drawn lit rather than ringed.
        ["Sound and lights"] = new[] { "leds" },
    };

    // The names on the diagram, so a part is called the same thing here as on
    // the editor's own picture of the device.
    static string PartName(string id) => id switch
    {
        "joystick" => Strings.Main_Joystick,
        "mp_left" => Strings.Main_LeftMouthpieceHole,
        "mp_center" => Strings.Main_CenterMouthpieceHole,
        "mp_right" => Strings.Main_RightMouthpieceHole,
        "side" => Strings.Main_SideTube,
        "lip" => Strings.Main_LipSwitch,
        "leds" => Strings.DevicePage_TheModeLightsOnThe,
        _ => id,
    };

    const double PadSize = 128;

    Control BuildDeviceBand()
    {
        var diagram = Diagram;
        _bandPhoto = new Canvas { Width = diagram.PhotoW, Height = diagram.PhotoH };
        _bandPad = new Canvas { Width = PadSize, Height = PadSize };

        var photo = new Viewbox
        {
            Child = _bandPhoto, Stretch = Stretch.Uniform,
            Height = 126, VerticalAlignment = VerticalAlignment.Center,
        };

        _bandParts = new TextBlock
        {
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        _bandRings = new TextBlock
        {
            FontSize = Size("SmallSize"), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
        };
        _bandLive = new TextBlock
        {
            FontSize = Size("SmallSize"), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
        };
        // Which buttons are down. Naming them is not possible from here: the
        // hole a button came from is decided by the profile the device has
        // loaded, in the mode it is in, and the report only carries the button
        // that came out the other end. A number still answers the question
        // somebody tuning a threshold is asking, which is whether the sip
        // registered at all.
        _bandPress = new TextBlock
        {
            FontSize = Size("SmallSize"), Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap,
        };
        // These move while nobody is looking at them, so a reader that
        // announced every change would talk over the app. They are here to be
        // found on demand, not read out.
        AutomationProperties.SetLiveSetting(_bandLive, AutomationLiveSetting.Off);
        AutomationProperties.SetLiveSetting(_bandPress, AutomationLiveSetting.Off);

        var words = new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { _bandParts, _bandRings, _bandLive, _bandPress },
        };

        // The pad is only about the joystick's own two settings, so it is only
        // on screen for the groups that hold them. On the other seven that room
        // goes to the words instead of to a diagram of something else.
        _bandPadBlock = new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _bandPad,
                new TextBlock
                {
                    Text = Strings.DevicePage_JoystickTravel,
                    FontSize = Size("SmallSize"), Classes = { "secondary" },
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        // A grid, not a stack: the words are the last column and take whatever
        // the photo and the pad leave, so the band has no empty right half on
        // the seven groups that do not draw a pad.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(_bandPadBlock, 1);
        Grid.SetColumn(words, 2);
        _bandPadBlock.Margin = new Thickness(20, 0, 0, 0);
        words.Margin = new Thickness(20, 0, 0, 0);
        row.Children.Add(photo);
        row.Children.Add(_bandPadBlock);
        row.Children.Add(words);

        var card = DeviceCard(row);
        card.Padding = new Thickness(16, 12);
        card.Margin = new Thickness(0, 0, 0, 14);
        return card;
    }

    // Redrawn whenever the open group changes, a joystick setting is edited, or
    // a live reading arrives. Cheap enough to do whole: both canvases hold
    // fewer than thirty shapes.
    void UpdateDeviceBand()
    {
        if (_bandPhoto is null || _bandPad is null) return;
        // A part the model does not have gets no ring: pointing at a lip switch
        // on a Singleton photo would be pointing at nothing.
        var parts = CategoryParts.GetValueOrDefault(_deviceCategory, Array.Empty<string>())
            .Where(p => p == "leds" || ModelHasZone(p)).ToArray();
        bool joystick = parts.Contains("joystick");
        DrawBandPhoto(parts);
        DrawBandPad();
        _bandPadBlock!.IsVisible = joystick;
        _bandRings!.IsVisible = joystick;
        _bandLive!.IsVisible = joystick;

        _bandParts!.Text = parts.Length == 0
            ? Strings.DevicePage_ThisGroupChangesNothingYou
            : string.Format(CultureInfo.CurrentCulture, Strings.DevicePage_ThisGroupChangesParts,
                string.Join(", ", parts.Select(PartName)));
    }

    void DrawBandPhoto(IReadOnlyList<string> parts)
    {
        var canvas = _bandPhoto!;
        var diagram = Diagram;
        canvas.Children.Clear();

        // Same crop the editor's diagram uses: the photo is laid out full size
        // inside a window clipped to the part worth showing, so a ring drawn at
        // a measured fraction lands on the same part in both pictures.
        var frame = new Canvas
        {
            Width = diagram.PhotoW, Height = diagram.PhotoH,
            ClipToBounds = true, IsHitTestVisible = false,
        };
        var photo = new Image
        {
            Source = DevicePhoto(_model),
            Width = diagram.FullSize.Width, Height = diagram.FullSize.Height,
            Stretch = Stretch.Fill, IsHitTestVisible = false,
        };
        Canvas.SetLeft(photo, diagram.FullOffset.X);
        Canvas.SetTop(photo, diagram.FullOffset.Y);
        frame.Children.Add(photo);
        canvas.Children.Add(frame);

        // The case lights, drawn at the brightness the settings file asks for,
        // because "LED brightness" is a number until you see it.
        if (parts.Contains("leds") && diagram.Lights is { } lightRow)
        {
            double bright = DeviceNumber("brightness", 75) / 100.0;
            for (int i = 0; i < 5; i++)
            {
                var led = diagram.OnPhoto(lightRow.X + i * lightRow.Gap, lightRow.Y);
                foreach (var dot in Led(ModeLight.Blue, led.X, led.Y))
                {
                    dot.Opacity *= Math.Clamp(bright, 0.08, 1);
                    canvas.Children.Add(dot);
                }
            }
        }

        foreach (var id in parts)
        {
            var spot = Array.Find(diagram.Hotspots, h => h.Zone == id);
            if (spot.Zone is null) continue;
            var at = diagram.OnPhoto(spot.PointX, spot.PointY);
            double x = at.X, y = at.Y;

            // Two rings: a wide dark one under a thin bright one, so the mark
            // survives both the black case and the pale mouthpiece.
            foreach (var (size, thickness, brush) in new[]
                { (46.0, 9.0, "Surface"), (46.0, 4.0, "Accent") })
            {
                var ring = new Ellipse
                {
                    Width = size, Height = size, StrokeThickness = thickness,
                    IsHitTestVisible = false,
                };
                BindBrush(ring, Shape.StrokeProperty, brush);
                Canvas.SetLeft(ring, x - size / 2);
                Canvas.SetTop(ring, y - size / 2);
                canvas.Children.Add(ring);
            }
        }

        DrawLiveStick(canvas);
    }

    // The stick, on the picture of the device, where the stick is. This is the
    // one live reading that is true in every profile: the QuadStick sends its
    // joystick as a gamepad's joystick whatever the mouthpiece is mapped to.
    //
    // It is the output, not the travel, which is why it is here and not on the
    // pad. The pad's two circles are a percent of how far the stick physically
    // moves; this is what comes out the other end after those are applied, and
    // drawing one on the other would put two different scales on one picture.
    void DrawLiveStick(Canvas canvas)
    {
        if (_live is not { } l) return;
        var spot = Array.Find(Diagram.Hotspots, h => h.Zone == "joystick");
        if (spot.Zone is null) return;
        var at = Diagram.OnPhoto(spot.PointX, spot.PointY);
        double cx = at.X, cy = at.Y, reach = 20;

        // The rest position, so a dot sitting still still says where centre is.
        var home = new Ellipse
        {
            Width = reach * 2, Height = reach * 2, StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 3 },
            IsHitTestVisible = false,
        };
        BindBrush(home, Shape.StrokeProperty, "Surface");
        Canvas.SetLeft(home, cx - reach);
        Canvas.SetTop(home, cy - reach);
        canvas.Children.Add(home);

        foreach (var (size, brush) in new[] { (20.0, "Surface"), (13.0, "Success") })
        {
            var dot = new Ellipse { Width = size, Height = size, IsHitTestVisible = false };
            BindBrush(dot, Shape.FillProperty, brush);
            Canvas.SetLeft(dot, cx + l.X * reach - size / 2);
            Canvas.SetTop(dot, cy + l.Y * reach - size / 2);
            canvas.Children.Add(dot);
        }
    }

    // The joystick's two settings as circles on a square of travel, with the
    // live stick position on top of them.
    void DrawBandPad()
    {
        var canvas = _bandPad!;
        canvas.Children.Clear();
        double half = PadSize / 2;

        var plate = new Rectangle
        {
            Width = PadSize, Height = PadSize, RadiusX = 10, RadiusY = 10,
            StrokeThickness = 1, IsHitTestVisible = false,
        };
        BindBrush(plate, Shape.FillProperty, "SurfaceSubtle");
        BindBrush(plate, Shape.StrokeProperty, "SurfaceBorder");
        canvas.Children.Add(plate);

        // Both are a percent of full travel, which is the edge of the square.
        double dead = Math.Clamp(DeviceNumber("joystick_deflection_minimum", 8), 0, 100) / 100.0;
        double full = Math.Clamp(DeviceNumber("joystick_deflection_maximum", 25), 1, 100) / 100.0;

        // Dashed for the dead zone, solid for full signal: a low-vision reader
        // gets two different lines, not two shades of the same one.
        foreach (var (fraction, dash, brush) in new[]
            { (dead, true, "TextSecondary"), (full, false, "Accent") })
        {
            double r = fraction * half;
            if (r < 1) continue;
            var ring = new Ellipse
            {
                Width = r * 2, Height = r * 2, StrokeThickness = 2, IsHitTestVisible = false,
                StrokeDashArray = dash ? new Avalonia.Collections.AvaloniaList<double> { 3, 3 } : null,
            };
            BindBrush(ring, Shape.StrokeProperty, brush);
            Canvas.SetLeft(ring, half - r);
            Canvas.SetTop(ring, half - r);
            canvas.Children.Add(ring);
        }

        AutomationProperties.SetName(canvas, Strings.DevicePage_HowFarTheStickHas);
        _bandRings!.Text = string.Format(CultureInfo.CurrentCulture,
            Strings.DevicePage_DashedRingDeadZone,
            (int)Math.Round(dead * 100), (int)Math.Round(full * 100));
        _bandLive!.Text = _live is { } l
            ? string.Format(CultureInfo.CurrentCulture, Strings.DevicePage_TheStickIsAtXY,
                (int)Math.Round(l.X * 100), (int)Math.Round(-l.Y * 100))
            : Strings.DevicePage_NothingIsReadingTheStick;

        _bandPress!.Text = _live is not { } now ? ""
            : now.Buttons.Count == 0
                ? string.Format(CultureInfo.CurrentCulture,
                    Strings.DevicePage_ReadingProductNothingPressed, now.Product)
                : string.Format(CultureInfo.CurrentCulture,
                    Strings.DevicePage_ReadingProductPressedNow, now.Product,
                    string.Join(", ", now.Buttons));
    }

    // What the settings file says a number is, or the catalog default when the
    // file does not carry it. Only the picture reads this: nothing here is
    // written back, so an unreadable value falls back instead of failing.
    double DeviceNumber(string name, double fallback)
    {
        if (_devicePreview.TryGetValue(name, out var preview)
            && double.TryParse(preview, NumberStyles.Integer, CultureInfo.InvariantCulture, out var previewValue))
            return previewValue;
        var sheet = DevicePrefsSheet;
        if (sheet is null || _devicePrefs is null) return fallback;
        foreach (var b in sheet.Bindings)
            if (string.Equals(b.Output, name, StringComparison.Ordinal))
                return double.TryParse(_devicePrefs.GetCell(b.Row, 1),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var def = PreferenceCatalog.All.FirstOrDefault(d => d.Name == name);
        return def is not null
            && double.TryParse(def.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known)
            ? known : fallback;
    }
}
