using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace QuadStick.App;

public enum OutputVisualKind
{
    Generic,
    DPad,
    FaceButton,
    KeyboardKeycap,
    Joystick,
    Mouse,
    Shoulder,
}

public enum OutputDirection
{
    N,
    NE,
    E,
    SE,
    S,
    SW,
    W,
    NW,
}

public enum ControllerFaceButton
{
    X,
    Circle,
    Square,
    Triangle,
    A,
    B,
    Y,
}

public enum ControllerPromptStyle
{
    Playstation,
    Xbox,
}

public enum ControllerStickSide
{
    Left,
    Right,
}

/// <summary>
/// Presentation metadata for one output token. Token remains the original
/// value; this type never replaces or normalizes the value written to a
/// profile.
/// </summary>
public sealed record OutputVisual(
    string Token,
    OutputVisualKind Kind,
    string FriendlyLabel,
    string? Symbol = null,
    OutputDirection? Direction = null,
    ControllerFaceButton? FaceButton = null,
    string? KeycapText = null,
    bool IsFallback = false,
    ControllerPromptStyle? PromptStyle = null,
    ControllerStickSide? StickSide = null,
    string? AssetKey = null,
    bool RequiresTextLabel = false)
{
    public string AccessibleName => FriendlyLabel.Length > 0 ? FriendlyLabel : Token;
    public bool IsStickClick { get; init; }

    /// <summary>True for L2/R2 (LT/RT), false for the L1/R1 (LB/RB) bar above
    /// it. Only meaningful on <see cref="OutputVisualKind.Shoulder"/>.</summary>
    public bool IsTrigger { get; init; }

    // These prompts carry their own meaning. Abstract firmware values keep
    // their words in the renderer instead of being reduced to an arbitrary
    // pictogram.
    public bool IsSelfDescribing => Kind is OutputVisualKind.DPad
        or OutputVisualKind.FaceButton
        or OutputVisualKind.KeyboardKeycap
        or OutputVisualKind.Joystick
        or OutputVisualKind.Mouse
        or OutputVisualKind.Shoulder;
}

/// <summary>
/// Resolves output families into a small set of parameterized visuals and
/// renders those visuals for the desktop UI. It deliberately does not contain
/// a per-token icon table.
/// </summary>
public static class OutputVisuals
{
    static readonly IReadOnlyDictionary<string, OutputDirection> DPadDirections =
        new Dictionary<string, OutputDirection>(StringComparer.Ordinal)
        {
            ["N"] = OutputDirection.N,
            ["NE"] = OutputDirection.NE,
            ["E"] = OutputDirection.E,
            ["SE"] = OutputDirection.SE,
            ["S"] = OutputDirection.S,
            ["SW"] = OutputDirection.SW,
            ["W"] = OutputDirection.W,
            ["NW"] = OutputDirection.NW,
        };

    public static OutputVisual For(string token) => For(token, null, null);

    /// <summary>
    /// The UI can provide its existing label policy (plain, Xbox-style, or
    /// raw). The resolver supplies a readable standalone label for tests and
    /// callers that do not have a window label policy.
    /// </summary>
    public static OutputVisual For(string token, Func<string, string>? friendlyLabel) =>
        For(token, friendlyLabel, null);

    /// <summary>
    /// Resolves presentation using the same controller prompt choice as the
    /// desktop label switch. This is presentation-only: Token is always the
    /// original raw output value.
    /// </summary>
    public static OutputVisual For(string token, Func<string, string>? friendlyLabel,
                                   ControllerPromptStyle? promptStyle)
    {
        var raw = token ?? "";
        var normalized = raw.Trim();

        if (normalized.StartsWith("dpad_", StringComparison.Ordinal)
            && DPadDirections.TryGetValue(normalized[5..], out var direction))
        {
            var label = Label(friendlyLabel, raw, DPadLabel(direction));
            return new(raw, OutputVisualKind.DPad, label,
                DirectionSymbol(direction), Direction: direction,
                PromptStyle: promptStyle ?? ControllerPromptStyle.Playstation,
                AssetKey: DPadAssetKey(promptStyle ?? ControllerPromptStyle.Playstation, direction));
        }

        if (TryFaceButton(normalized, out var face, out var faceSymbol))
        {
            var label = Label(friendlyLabel, raw, FaceButtonLabel(face));
            // Existing Xbox-style labels say "A button", "B button", etc.
            // Follow that policy in the visual while retaining the token's
            // original PlayStation/Xbox meaning in the model.
            if (friendlyLabel is not null)
                faceSymbol = XboxSymbolIfNamed(label, faceSymbol);
            var style = promptStyle ?? (IsXboxToken(normalized) || IsXboxLabel(label)
                ? ControllerPromptStyle.Xbox
                : ControllerPromptStyle.Playstation);
            return new(raw, OutputVisualKind.FaceButton, label, faceSymbol,
                FaceButton: face, PromptStyle: style,
                AssetKey: FaceButtonAssetKey(style, face, normalized));
        }

        if (TryShoulder(normalized, out var shoulderSide, out var isTrigger, out var xboxVocab))
        {
            var style = promptStyle ?? (xboxVocab
                ? ControllerPromptStyle.Xbox
                : ControllerPromptStyle.Playstation);
            var label = Label(friendlyLabel, raw, ShoulderLabel(shoulderSide, isTrigger));
            return new(raw, OutputVisualKind.Shoulder, label,
                ShoulderMarking(style, shoulderSide, isTrigger),
                StickSide: shoulderSide, PromptStyle: style)
            {
                IsTrigger = isTrigger,
            };
        }

        if (TryStick(normalized, out var side, out var stickDirection))
        {
            var label = Label(friendlyLabel, raw, StickLabel(side, stickDirection));
            var style = promptStyle ?? ControllerPromptStyle.Playstation;
            var isStickClick = stickDirection is null;
            return new(raw, OutputVisualKind.Joystick, label,
                stickDirection is { } d ? DirectionSymbol(d) : null,
                Direction: stickDirection, StickSide: side, PromptStyle: style,
                AssetKey: isStickClick ? StickAssetKey(style, side) : null,
                // The drawing says both halves now: a letter beside the well
                // for the stick, a wedge on the rim for the direction. Words
                // beside it would only repeat what the picture already shows,
                // and in the 30% detail panel they ran off the edge.
                RequiresTextLabel: false)
            {
                IsStickClick = isStickClick,
            };
        }

        if (TryMouse(normalized, out var mouseAsset, out var requiresTextLabel))
        {
            var label = Label(friendlyLabel, raw, Humanize(normalized));
            return new(raw, OutputVisualKind.Mouse, label, AssetKey: mouseAsset,
                RequiresTextLabel: requiresTextLabel);
        }

        if (normalized.StartsWith("kb_", StringComparison.Ordinal))
        {
            var keycap = KeycapText(normalized[3..]);
            // The existing generic Humanize helper produces "Kb a". A
            // generated keycap has a better readable name, so keyboard labels
            // use the same generated text instead of inventing one-off labels.
            return new(raw, OutputVisualKind.KeyboardKeycap, keycap,
                KeycapText: keycap, AssetKey: KeyboardAssetKey(normalized[3..]));
        }

        var fallback = Label(friendlyLabel, raw, Humanize(normalized));
        return new(raw, OutputVisualKind.Generic, fallback, "?", IsFallback: true);
    }

    public static Control Create(OutputVisual visual, string? label = null, bool includeLabel = true,
                                 bool compact = false) =>
        Render(visual, label, includeLabel, compact);

    /// <summary>Creates one non-interactive, accessible output presentation.</summary>
    public static Control Render(OutputVisual visual, string? label = null, bool includeLabel = true,
                                 bool compact = false)
    {
        var text = label ?? visual.FriendlyLabel;
        var spoken = text.Length > 0 ? text : visual.AccessibleName;

        // "Decrement mode" is a thing the firmware does, not a thing on a
        // controller. There is no picture of it, and the neutral box that used
        // to sit beside the words drew a control the device does not have.
        // These read as words, alone.
        if (!visual.IsSelfDescribing)
        {
            var words = new TextBlock
            {
                Text = spoken,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetName(words, spoken);
            return words;
        }

        // A real controller prompt or keycap already says what it is. Keep a
        // wrapper for layout and for the name read aloud, but do not repeat
        // its name beside the artwork.
        var visualPart = RenderVisual(visual, compact);
        if (!includeLabel) return visualPart;

        // A whole-mouse silhouette cannot say whether it moves left, right,
        // pans, or moves vertically. So it earns an adjacent readable label.
        // Stick movement does too: L/R inside a 30px thumb cap was invisible.
        if (visual.RequiresTextLabel)
        {
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = compact ? 4 : 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            content.Children.Add(visualPart);
            content.Children.Add(new TextBlock
            {
                Text = spoken,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            AutomationProperties.SetName(content, spoken);
            return content;
        }

        var iconOnly = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        iconOnly.Children.Add(visualPart);
        AutomationProperties.SetName(iconOnly, spoken);
        return iconOnly;
    }

    // Only ever reached for a kind that has artwork; Render sends everything
    // else to words before it gets here.
    static Control RenderVisual(OutputVisual visual, bool compact)
    {
        Control control = visual.Kind switch
        {
            OutputVisualKind.DPad => DPad(visual, compact),
            OutputVisualKind.FaceButton => FaceButton(visual, compact),
            OutputVisualKind.KeyboardKeycap => Keyboard(visual, compact),
            OutputVisualKind.Mouse => Mouse(visual, compact),
            OutputVisualKind.Shoulder => Shoulder(visual, compact),
            _ => Joystick(visual, compact),
        };
        control.IsHitTestVisible = false;
        AutomationProperties.SetName(control, visual.AccessibleName);
        return control;
    }

    static Control DPad(OutputVisual visual, bool compact)
    {
        var direction = visual.Direction!.Value;
        var style = visual.PromptStyle ?? ControllerPromptStyle.Playstation;
        var asset = DPadAssetKey(style, direction);
        var prompt = Asset(asset);
        if (prompt is not null)
        {
            if (direction is OutputDirection.N or OutputDirection.E
                or OutputDirection.S or OutputDirection.W)
                return VectorBox(prompt, compact ? 30 : 48);

            // Xelu supplies highlighted cardinal D-pads and no diagonals. The
            // authentic pad keeps the body; the corner it leaves empty is
            // where the diagonal is said, at a weight that survives being
            // drawn 30px wide in a list cell.
            var composed = new Canvas { Width = 256, Height = 256 };
            composed.Children.Add(prompt);
            var (dx, dy) = DirectionVector(direction);
            AddCornerWedge(composed, dx, dy);
            return VectorBox(composed, compact ? 30 : 48);
        }

        return DPadFallback(direction, compact);
    }

    static Control DPadFallback(OutputDirection direction, bool compact)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var pad = new Polygon
        {
            Points = new Points
            {
                new(8, 1), new(16, 1), new(16, 8), new(23, 8), new(23, 16),
                new(16, 16), new(16, 23), new(8, 23), new(8, 16), new(1, 16),
                new(1, 8), new(8, 8),
            },
            StrokeThickness = 1.25,
        };
        Bind(pad, Shape.FillProperty, "SurfaceSubtle");
        Bind(pad, Shape.StrokeProperty, "TextSecondary");
        canvas.Children.Add(pad);

        var (dx, dy) = DirectionVector(direction);
        var tip = new Point(12 + dx * 7, 12 + dy * 7);
        var left = new Point(tip.X - dx * 3 - dy * 2.5, tip.Y - dy * 3 + dx * 2.5);
        var right = new Point(tip.X - dx * 3 + dy * 2.5, tip.Y - dy * 3 - dx * 2.5);
        AddLine(canvas, new Point(12 + dx * 1.5, 12 + dy * 1.5), tip, "Accent", 2.1);
        AddLine(canvas, left, tip, "Accent", 2.1);
        AddLine(canvas, tip, right, "Accent", 2.1);
        return VectorBox(canvas, compact ? 18 : 24);
    }

    static (double X, double Y) DirectionVector(OutputDirection direction) => direction switch
    {
        OutputDirection.N => (0, -1),
        OutputDirection.NE => (0.707, -0.707),
        OutputDirection.E => (1, 0),
        OutputDirection.SE => (0.707, 0.707),
        OutputDirection.S => (0, 1),
        OutputDirection.SW => (-0.707, 0.707),
        OutputDirection.W => (-1, 0),
        _ => (-0.707, -0.707),
    };

    static Control FaceButton(OutputVisual visual, bool compact)
    {
        var prompt = Asset(visual.AssetKey);
        if (prompt is not null)
            return VectorBox(prompt, compact ? 30 : 48);

        return FaceButtonFallback(visual.FaceButton!.Value, visual.Symbol ?? "", compact);
    }

    static Control FaceButtonFallback(ControllerFaceButton button, string symbol, bool compact)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        switch (button)
        {
            case ControllerFaceButton.Circle:
            case ControllerFaceButton.A:
            case ControllerFaceButton.B:
            case ControllerFaceButton.Y:
                var circle = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1.5 };
                Bind(circle, Shape.FillProperty, "SurfaceSubtle");
                Bind(circle, Shape.StrokeProperty, "TextSecondary");
                Canvas.SetLeft(circle, 3); Canvas.SetTop(circle, 3);
                canvas.Children.Add(circle);
                if (button is ControllerFaceButton.A or ControllerFaceButton.B
                    or ControllerFaceButton.Y)
                    canvas.Children.Add(CenteredLetter(symbol));
                break;
            case ControllerFaceButton.X:
                if (symbol == "×")
                {
                    AddLine(canvas, new Point(6, 6), new Point(18, 18), "TextSecondary", 2.1);
                    AddLine(canvas, new Point(18, 6), new Point(6, 18), "TextSecondary", 2.1);
                }
                else
                {
                    var xCircle = new Ellipse { Width = 18, Height = 18, StrokeThickness = 1.5 };
                    Bind(xCircle, Shape.FillProperty, "SurfaceSubtle");
                    Bind(xCircle, Shape.StrokeProperty, "TextSecondary");
                    Canvas.SetLeft(xCircle, 3); Canvas.SetTop(xCircle, 3);
                    canvas.Children.Add(xCircle);
                    canvas.Children.Add(CenteredLetter(symbol));
                }
                break;
            case ControllerFaceButton.Square:
                var square = new Rectangle
                {
                    Width = 17, Height = 17, RadiusX = 3, RadiusY = 3, StrokeThickness = 1.5,
                };
                Bind(square, Shape.FillProperty, "SurfaceSubtle");
                Bind(square, Shape.StrokeProperty, "TextSecondary");
                Canvas.SetLeft(square, 3.5); Canvas.SetTop(square, 3.5);
                canvas.Children.Add(square);
                break;
            case ControllerFaceButton.Triangle:
                var triangle = new Polygon
                {
                    Points = new Points { new(12, 3), new(21, 20), new(3, 20) },
                    StrokeThickness = 1.5,
                };
                Bind(triangle, Shape.FillProperty, "SurfaceSubtle");
                Bind(triangle, Shape.StrokeProperty, "TextSecondary");
                canvas.Children.Add(triangle);
                break;
        }

        return VectorBox(canvas, compact ? 30 : 48);
    }

    static Control Keyboard(OutputVisual visual, bool compact)
    {
        var prompt = Asset(visual.AssetKey);
        return prompt is not null
            ? VectorBox(prompt, compact ? 30 : 48)
            : Keycap(visual.KeycapText ?? visual.FriendlyLabel, compact);
    }

    static Control Mouse(OutputVisual visual, bool compact)
    {
        var prompt = Asset(visual.AssetKey);
        return prompt is not null
            ? VectorBox(prompt, compact ? 30 : 48)
            : Keycap(visual.FriendlyLabel, compact);
    }

    static Control Joystick(OutputVisual visual, bool compact)
    {
        var side = visual.StickSide ?? ControllerStickSide.Left;
        double box = compact ? 30 : 48;

        if (visual.IsStickClick)
        {
            // This is the one place where Xelu's L/R stick prompt is correct:
            // the raw left_stick/right_stick outputs mean pressing the stick.
            var prompt = Asset(visual.AssetKey);
            if (prompt is not null)
                return WithStickSide(VectorBox(prompt, box), side, box);
        }

        // Xelu's L/R stick artwork is specifically the "press the stick"
        // prompt. It is the wrong semantic for left_joy_* and right_joy_*;
        // those continue through the physical analog-stick construction below.
        // Render a quiet analog-stick mechanism instead: a recessed well,
        // socket, stem, and displaced thumb cap. The cap's position is the
        // direction; there is no arrow or annotation laid over the control.
        //
        var canvas = new Canvas { Width = 256, Height = 256 };

        var well = new Ellipse
        {
            Width = 190, Height = 190, StrokeThickness = 7,
        };
        Bind(well, Shape.FillProperty, "SurfaceBorder");
        Bind(well, Shape.StrokeProperty, "TextSecondary");
        Canvas.SetLeft(well, 33); Canvas.SetTop(well, 33);
        canvas.Children.Add(well);

        var wellFace = new Ellipse
        {
            Width = 150, Height = 150, StrokeThickness = 4,
        };
        Bind(wellFace, Shape.FillProperty, "SurfaceSubtle");
        Bind(wellFace, Shape.StrokeProperty, "SurfaceBorder");
        Canvas.SetLeft(wellFace, 53); Canvas.SetTop(wellFace, 53);
        canvas.Children.Add(wellFace);

        var socket = new Ellipse { Width = 64, Height = 64 };
        Bind(socket, Shape.FillProperty, "Surface");
        Canvas.SetLeft(socket, 96); Canvas.SetTop(socket, 96);
        canvas.Children.Add(socket);

        if (visual.Direction is { } direction)
        {
            var (dx, dy) = DirectionVector(direction);
            // The cap stays home and a wedge on the rim says which way, the way
            // the d-pad does it. Sliding the cap instead moved it under five
            // pixels at 30px and all four directions read as one picture; any
            // further and it climbed out of its own well.
            AddRimWedge(canvas, dx, dy);
            var target = new Point(128, 128);
            AddStickCap(canvas, target);
        }
        else
        {
            AddStickCap(canvas, new Point(128, 128));
        }

        return WithStickSide(VectorBox(canvas, box), side, box);
    }

    // Xelu ships one stick picture for both sticks and leaves left or right to
    // the caption, and the drawn mechanism is symmetric, so left_joy_left and
    // right_joy_left came out as the same picture. The letter is the only thing
    // that tells them apart. It sits outside the well, where it keeps a usable
    // size; inside the cap it would shrink to a smudge at 30px.
    static Control WithStickSide(Control stick, ControllerStickSide side, double box)
    {
        var letter = new TextBlock
        {
            Text = side == ControllerStickSide.Right ? "R" : "L",
            FontWeight = FontWeight.Bold,
            FontSize = box * 0.52,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, box * 0.1, 0),
        };
        Bind(letter, TextBlock.ForegroundProperty, "TextSecondary");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(letter);
        row.Children.Add(stick);
        return row;
    }

    // Sized for this 256 artboard, unlike the d-pad's corner wedge: a cardinal
    // wedge at the d-pad's radius would have its tip at 306 and the Viewbox
    // would cut it in half.
    static void AddRimWedge(Canvas canvas, double dx, double dy)
    {
        var tip = new Point(128 + dx * 125, 128 + dy * 125);
        var back = new Point(128 + dx * 97, 128 + dy * 97);
        var px = -dy * 28;
        var py = dx * 28;
        var wedge = new Polygon
        {
            Points = new Points
            {
                tip, new(back.X + px, back.Y + py), new(back.X - px, back.Y - py),
            },
            IsHitTestVisible = false,
        };
        Bind(wedge, Shape.FillProperty, "Accent");
        canvas.Children.Add(wedge);
    }

    static void AddStickCap(Canvas canvas, Point center)
    {
        var puck = new Ellipse { Width = 78, Height = 78, StrokeThickness = 5 };
        Bind(puck, Shape.FillProperty, "Accent");
        Bind(puck, Shape.StrokeProperty, "SurfaceBorder");
        Canvas.SetLeft(puck, center.X - 39);
        Canvas.SetTop(puck, center.Y - 39);
        canvas.Children.Add(puck);

        var grip = new Ellipse { Width = 52, Height = 52 };
        Bind(grip, Shape.FillProperty, "SurfaceSubtle");
        Canvas.SetLeft(grip, center.X - 26);
        Canvas.SetTop(grip, center.Y - 26);
        canvas.Children.Add(grip);

    }

    // Xelu's pack stops at the face buttons, the pad and the sticks, so the
    // shoulder row has no photograph to load and is drawn. A bumper is the
    // flat bar; a trigger is the deeper paddle behind it. That difference in
    // outline is what tells them apart without reading the marking, which is
    // the same rule the rest of this file follows: never colour alone.
    static Control Shoulder(OutputVisual visual, bool compact)
    {
        var height = compact ? 30d : 48d;
        var width = visual.IsTrigger ? (compact ? 30d : 46d) : (compact ? 42d : 62d);
        var bodyHeight = visual.IsTrigger ? height : height * 0.6;
        var body = new Border
        {
            Width = width,
            Height = bodyHeight,
            CornerRadius = visual.IsTrigger
                ? new CornerRadius(width * 0.46, width * 0.46, width * 0.16, width * 0.16)
                : new CornerRadius(bodyHeight / 2),
            BorderThickness = new Thickness(compact ? 1 : 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = visual.Symbol ?? "",
                FontSize = compact ? 12 : 18,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // The trigger curves at the top, so its marking sits in the
                // flat half where the moulded letters are on the hardware.
                Margin = visual.IsTrigger
                    ? new Thickness(0, height * 0.2, 0, 0)
                    : default,
            },
        };
        Bind(body, Border.BackgroundProperty, "SurfaceSubtle");
        Bind(body, Border.BorderBrushProperty, "TextSecondary");

        var plate = new Grid { Width = width, Height = height };
        plate.Children.Add(body);
        return plate;
    }

    static Control Keycap(string text, bool compact)
    {
        // Keycaps are a generated visual, so their minimum optical weight
        // matches the authentic controller prompts. The label stays inside
        // the keycap; it is not repeated beside it.
        // A fallback keycap must be a real label, not a tiny icon with an
        // ellipsis. This matters for tokens such as "Keyboard" and
        // "Page Down", whose artwork may be unavailable. Keep the compact
        // version narrow enough for mapping cards; genuinely long names wrap
        // inside the keycap instead of overflowing the output cell.
        var height = compact ? 42 : 56;
        var width = compact ? 58 : 86;
        var plate = new Grid { Width = width, Height = height };
        var back = new Border
        {
            Margin = new Thickness(0, compact ? 3 : 4, 0, 0),
            CornerRadius = new CornerRadius(compact ? 6 : 9),
        };
        Bind(back, Border.BackgroundProperty, "SurfaceBorder");
        Bind(back, Border.BorderBrushProperty, "SurfaceBorder");
        plate.Children.Add(back);
        var front = new Border
        {
            Margin = new Thickness(0, 0, 0, compact ? 3 : 4),
            CornerRadius = new CornerRadius(compact ? 6 : 9),
            Padding = new Thickness(compact ? 8 : 12, 4), Child = new TextBlock
            {
                Text = text,
                FontSize = compact ? 12 : 18,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Bind(front, Border.BackgroundProperty, "SurfaceSubtle");
        Bind(front, Border.BorderBrushProperty, "TextSecondary");
        front.BorderThickness = new Thickness(1);
        plate.Children.Add(front);
        return plate;
    }

    static TextBlock CenteredLetter(string symbol) => new()
    {
        Text = symbol, Width = 24, Height = 24, FontSize = 10,
        FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 5, 0, 0),
    };

    static void AddLine(Canvas canvas, Point from, Point to, string brush, double thickness)
    {
        var line = new Line
        {
            StartPoint = from, EndPoint = to, StrokeThickness = thickness,
            IsHitTestVisible = false,
        };
        Bind(line, Shape.StrokeProperty, brush);
        canvas.Children.Add(line);
    }

    // A solid wedge in the corner the cross leaves empty. A stroked arrow at
    // this size came out as a smudge once the 256 artboard was drawn 30px wide
    // in a list cell.
    static void AddCornerWedge(Canvas canvas, double dx, double dy)
    {
        var tip = new Point(128 + dx * 178, 128 + dy * 178);
        var back = new Point(128 + dx * 104, 128 + dy * 104);
        var px = -dy * 46;
        var py = dx * 46;
        var wedge = new Polygon
        {
            Points = new Points
            {
                tip, new(back.X + px, back.Y + py), new(back.X - px, back.Y - py),
            },
            IsHitTestVisible = false,
        };
        Bind(wedge, Shape.FillProperty, "Accent");
        canvas.Children.Add(wedge);
    }

    static Control VectorBox(Control child, double size) => new Viewbox
    {
        Width = size, Height = size, Stretch = Stretch.Uniform, Child = child,
    };

    static void Bind(Control target, AvaloniaProperty property, string tokenKey) =>
        target[!property] = new DynamicResourceExtension(tokenKey + "Brush");

    static readonly Dictionary<string, Bitmap> BitmapCache = new(StringComparer.Ordinal);

    static Control? Asset(string? assetKey)
    {
        if (string.IsNullOrWhiteSpace(assetKey)) return null;
        try
        {
            if (AssetPath(assetKey) is not { } art) return null;
            var cacheKey = IsDarkTheme ? $"{art.Path}:dark" : $"{art.Path}:light";
            if (!BitmapCache.TryGetValue(cacheKey, out var bitmap))
            {
                using var stream = AssetLoader.Open(new Uri(
                    $"avares://QuadStickConfigManager/Assets/OutputVisuals/{art.Path}"));
                bitmap = new Bitmap(stream);
                BitmapCache[cacheKey] = bitmap;
            }

            var image = new Image
            {
                Source = bitmap,
                Width = 256,
                Height = 256,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
            };
            if (art.Rotate != 0)
            {
                image.RenderTransform = new RotateTransform(art.Rotate);
                image.RenderTransformOrigin = RelativePoint.Center;
            }

            return image;
        }
        catch (Exception)
        {
            // A missing optional art asset must never make a profile or picker
            // unusable. The caller falls back to the parameterized geometry.
            return null;
        }
    }

    /// <summary>
    /// The art file behind one asset key, and the turn to give it. Internal so
    /// a test can prove every key the resolver hands out reaches a real file.
    /// </summary>
    internal static (string Path, int Rotate)? AssetPath(string assetKey)
    {
        if (assetKey.StartsWith("ps:", StringComparison.Ordinal))
        {
            string? name = assetKey[3..] switch
            {
                "circle" => "Playstation/Playstation0021.png",
                "triangle" => "Playstation/Playstation0022.png",
                "square" => "Playstation/Playstation0023.png",
                "cross" => "Playstation/Playstation0024.png",
                "left-stick" => "Playstation/Playstation0027.png",
                "right-stick" => "Playstation/Playstation0028.png",
                "dpad" => "Playstation/Playstation0029.png",
                "dpad-n" => "Playstation/Playstation0030.png",
                "dpad-e" => "Playstation/Playstation0031.png",
                "dpad-s" => "Playstation/Playstation0032.png",
                "dpad-w" => "Playstation/Playstation0033.png",
                _ => null,
            };
            return name is null ? null : (name, 0);
        }

        if (assetKey.StartsWith("xbox:", StringComparison.Ordinal))
        {
            // The Xbox files run two ahead of the PlayStation set: 0007 is the
            // left stick, not the d-pad. Reading them in PlayStation order put
            // a thumbstick under "d-pad north" and left south and west with no
            // file at all, so both fell back to the drawn stand-in.
            return assetKey[5..] switch
            {
                "a" => ("Xbox/Xbox0001.png", 0),
                "b" => ("Xbox/Xbox0002.png", 0),
                "y" => ("Xbox/Xbox0003.png", 0),
                "x" => ("Xbox/Xbox0004.png", 0),
                "left-stick" => ("Xbox/Xbox0007.png", 0),
                "right-stick" => ("Xbox/Xbox0008.png", 0),
                "dpad" => ("Xbox/Xbox0009.png", 0),
                "dpad-n" => ("Xbox/Xbox0010.png", 0),
                "dpad-e" => ("Xbox/Xbox0011.png", 0),
                // Xelu's Xbox set stops at north and east. A d-pad cross is
                // symmetric under half a turn, so those two rotated are the
                // real artwork rather than a second-hand drawing of it.
                "dpad-s" => ("Xbox/Xbox0010.png", 180),
                "dpad-w" => ("Xbox/Xbox0011.png", 180),
                _ => ((string, int)?)null,
            };
        }

        if (assetKey.StartsWith("keyboard:", StringComparison.Ordinal)
            && int.TryParse(assetKey[9..], out var id))
        {
            var folder = IsDarkTheme ? "KeyboardDark" : "KeyboardLight";
            var prefix = IsDarkTheme ? "KeyDark" : "KeyLight";
            return ($"{folder}/{prefix}{id:0000}.png", 0);
        }

        if (assetKey.StartsWith("mouse:", StringComparison.Ordinal)
            && int.TryParse(assetKey[6..], out var mouseId))
        {
            var folder = IsDarkTheme ? "KeyboardDark" : "KeyboardLight";
            var prefix = IsDarkTheme ? "KeyDark" : "KeyLight";
            return ($"{folder}/{prefix}{mouseId:0000}.png", 0);
        }

        return null;
    }

    static bool IsDarkTheme => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    static string DPadAssetKey(ControllerPromptStyle style, OutputDirection direction)
    {
        var prefix = style == ControllerPromptStyle.Xbox ? "xbox" : "ps";
        return direction switch
        {
            OutputDirection.N => $"{prefix}:dpad-n",
            OutputDirection.E => $"{prefix}:dpad-e",
            OutputDirection.S => $"{prefix}:dpad-s",
            OutputDirection.W => $"{prefix}:dpad-w",
            // No console prompt exists for a diagonal. The neutral pad carries
            // the indicator; the old key spelled "dpad-dpad" and matched no
            // file, so every diagonal fell out to the drawn stand-in.
            _ => $"{prefix}:dpad",
        };
    }

    // Read off the token, never off the label. The label is translated: in a
    // language whose word for the B button does not start with a B, matching
    // on its first letter put the same X button under all four faces. The
    // token is a file byte and says the same thing in every language.
    static string FaceButtonAssetKey(ControllerPromptStyle style,
                                     ControllerFaceButton face, string token)
    {
        // The two pads' faces, paired by where the thumb lands.
        var (ps, xbox) = token switch
        {
            "x" or "A" => ("cross", "a"),
            "circle" or "B" => ("circle", "b"),
            "square" or "X" => ("square", "x"),
            "triangle" or "Y" => ("triangle", "y"),
            _ => face switch
            {
                ControllerFaceButton.Circle => ("circle", "b"),
                ControllerFaceButton.Square => ("square", "x"),
                ControllerFaceButton.Triangle => ("triangle", "y"),
                ControllerFaceButton.A => ("cross", "a"),
                ControllerFaceButton.B => ("circle", "b"),
                ControllerFaceButton.Y => ("triangle", "y"),
                _ => ("square", "x"),
            },
        };
        return style == ControllerPromptStyle.Xbox ? $"xbox:{xbox}" : $"ps:{ps}";
    }

    static string StickAssetKey(ControllerPromptStyle style, ControllerStickSide side) =>
        $"{(style == ControllerPromptStyle.Xbox ? "xbox" : "ps")}:{(side == ControllerStickSide.Left ? "left" : "right")}-stick";

    static string? KeyboardAssetKey(string key)
    {
        var lower = key.ToLowerInvariant();
        const string qwerty = "qwertyuiopasdfghjklzxcvbnm";
        var letter = qwerty.IndexOf(lower, StringComparison.Ordinal);
        if (lower.Length == 1 && letter >= 0)
            return $"keyboard:{85 + letter}";
        if (lower.Length == 1 && lower[0] is >= '0' and <= '9')
            return $"keyboard:{111 + (lower[0] == '0' ? 0 : lower[0] - '0')}";

        var special = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["grave_accent_and_tilde"] = 121,
            ["insert"] = 122, ["delete"] = 123, ["end"] = 124, ["home"] = 125,
            ["page_down"] = 126, ["page_up"] = 127, ["minus"] = 128,
            ["equal"] = 129, ["slash"] = 130, ["escape"] = 139,
            ["left_shift"] = 140, ["left_control"] = 141, ["left_alt"] = 142,
            ["tab"] = 143, ["caps_lock"] = 144, ["backspace"] = 145,
            ["left_arrow"] = 146, ["space"] = 148, ["enter"] = 149,
            ["return"] = 150,
        };
        return special.TryGetValue(lower, out var id) ? $"keyboard:{id}" : null;
    }

    static bool TryMouse(string token, out string assetKey, out bool requiresTextLabel)
    {
        requiresTextLabel = false;
        var id = token switch
        {
            "mouse_left_button" => 69,
            "mouse_right_button" => 70,
            "mouse_middle_button" => 71,
            "mouse_wheel_up" => 72,
            "mouse_wheel_down" => 73,
            "mouse_back" => 75,
            "mouse_forward" => 76,
            // The basic mouse silhouette has no directional meaning of its
            // own, so Render keeps the action's words alongside it.
            "mouse_left" or "mouse_right" or "mouse_up" or "mouse_down"
                or "mouse_pan_left" or "mouse_pan_right" => 68,
            _ => 0,
        };
        requiresTextLabel = id == 68;
        assetKey = id == 0 ? "" : $"mouse:{id:0000}";
        return id != 0;
    }

    static bool IsXboxToken(string token) => token is "A" or "B" or "X" or "Y";

    static bool IsXboxLabel(string label) =>
        label.StartsWith("A ", StringComparison.CurrentCultureIgnoreCase)
        || label.StartsWith("B ", StringComparison.CurrentCultureIgnoreCase)
        || label.StartsWith("X ", StringComparison.CurrentCultureIgnoreCase)
        || label.StartsWith("Y ", StringComparison.CurrentCultureIgnoreCase);

    static bool TryStick(string token, out ControllerStickSide side,
                         out OutputDirection? direction)
    {
        side = token.StartsWith("right_joy_", StringComparison.Ordinal)
            || token is "right_stick" or "right_3"
                ? ControllerStickSide.Right : ControllerStickSide.Left;
        direction = token switch
        {
            "left_joy_left" or "right_joy_left" => OutputDirection.W,
            "left_joy_right" or "right_joy_right" => OutputDirection.E,
            "left_joy_up" or "right_joy_up" => OutputDirection.N,
            "left_joy_down" or "right_joy_down" => OutputDirection.S,
            _ => null,
        };
        // left_3/right_3 is what a PS3 profile calls pressing the stick;
        // left_stick/right_stick is the Xbox vocabulary for the same button.
        // Only the second one was listed, so the press prompt never once
        // reached a PS3 profile, which is most of them.
        return direction is not null
            || token is "left_stick" or "right_stick" or "left_3" or "right_3";
    }

    // The shoulder row under both output vocabularies: a PS3 profile spells it
    // left_1/left_2, an Xbox one left_bumper/left_trigger. Same four physical
    // buttons, so they share one visual.
    static bool TryShoulder(string token, out ControllerStickSide side,
                            out bool isTrigger, out bool xboxVocab)
    {
        var known = token switch
        {
            "left_1" => (ControllerStickSide.Left, false, false),
            "right_1" => (ControllerStickSide.Right, false, false),
            "left_2" => (ControllerStickSide.Left, true, false),
            "right_2" => (ControllerStickSide.Right, true, false),
            "left_bumper" => (ControllerStickSide.Left, false, true),
            "right_bumper" => (ControllerStickSide.Right, false, true),
            "left_trigger" => (ControllerStickSide.Left, true, true),
            "right_trigger" => (ControllerStickSide.Right, true, true),
            _ => ((ControllerStickSide Side, bool Trigger, bool Xbox)?)null,
        };
        (side, isTrigger, xboxVocab) = known ?? (default, false, false);
        return known is not null;
    }

    static bool TryFaceButton(string token, out ControllerFaceButton face, out string symbol)
    {
        switch (token)
        {
            case "x": face = ControllerFaceButton.X; symbol = "×"; return true;
            case "circle": face = ControllerFaceButton.Circle; symbol = "○"; return true;
            case "square": face = ControllerFaceButton.Square; symbol = "□"; return true;
            case "triangle": face = ControllerFaceButton.Triangle; symbol = "△"; return true;
            case "A": face = ControllerFaceButton.A; symbol = "A"; return true;
            case "B": face = ControllerFaceButton.B; symbol = "B"; return true;
            case "X": face = ControllerFaceButton.X; symbol = "X"; return true;
            case "Y": face = ControllerFaceButton.Y; symbol = "Y"; return true;
            default: face = default; symbol = ""; return false;
        }
    }

    static string XboxSymbolIfNamed(string label, string fallback) =>
        label.StartsWith("A ", StringComparison.CurrentCultureIgnoreCase) ? "A"
        : label.StartsWith("B ", StringComparison.CurrentCultureIgnoreCase) ? "B"
        : label.StartsWith("X ", StringComparison.CurrentCultureIgnoreCase) ? "X"
        : label.StartsWith("Y ", StringComparison.CurrentCultureIgnoreCase) ? "Y"
        : fallback;

    static string Label(Func<string, string>? friendlyLabel, string token, string fallback)
    {
        var label = friendlyLabel?.Invoke(token) ?? fallback;
        return label.Length > 0 ? label : fallback;
    }

    static string DPadLabel(OutputDirection direction) => $"D-pad {direction switch
    {
        OutputDirection.N => "north",
        OutputDirection.NE => "northeast",
        OutputDirection.E => "east",
        OutputDirection.SE => "southeast",
        OutputDirection.S => "south",
        OutputDirection.SW => "southwest",
        OutputDirection.W => "west",
        _ => "northwest",
    }}";

    static string StickLabel(ControllerStickSide side, OutputDirection? direction)
    {
        var name = side == ControllerStickSide.Left ? Strings.Main_LeftStick : Strings.Main_RightStick;
        return direction is { } d ? $"{name} {d switch
        {
            OutputDirection.N => "up",
            OutputDirection.E => "right",
            OutputDirection.S => "down",
            _ => "left",
        }}" : side == ControllerStickSide.Left
            ? Strings.Main_LeftStickClick
            : Strings.Main_RightStickClick;
    }

    // What is moulded on the plastic. Read off the prompt style, never off the
    // label: the label is translated, and this is a hardware marking that says
    // the same thing in every language.
    static string ShoulderMarking(ControllerPromptStyle style, ControllerStickSide side,
                                  bool isTrigger)
    {
        var letter = side == ControllerStickSide.Left ? "L" : "R";
        return style == ControllerPromptStyle.Xbox
            ? letter + (isTrigger ? "T" : "B")
            : letter + (isTrigger ? "2" : "1");
    }

    static string ShoulderLabel(ControllerStickSide side, bool isTrigger) =>
        side == ControllerStickSide.Left
            ? isTrigger ? Strings.Main_LeftTrigger : Strings.Main_LeftBumper
            : isTrigger ? Strings.Main_RightTrigger : Strings.Main_RightBumper;

    static string FaceButtonLabel(ControllerFaceButton button) => button switch
    {
        ControllerFaceButton.Circle => "Circle",
        ControllerFaceButton.Square => "Square",
        ControllerFaceButton.Triangle => "Triangle",
        ControllerFaceButton.A => Strings.Main_AButton,
        ControllerFaceButton.B => Strings.Main_BButton,
        ControllerFaceButton.Y => Strings.Main_YButton,
        _ => Strings.Main_XButton,
    };

    static string DirectionSymbol(OutputDirection direction) => direction switch
    {
        OutputDirection.N => "↑",
        OutputDirection.NE => "↗",
        OutputDirection.E => "→",
        OutputDirection.SE => "↘",
        OutputDirection.S => "↓",
        OutputDirection.SW => "↙",
        OutputDirection.W => "←",
        _ => "↖",
    };

    static string KeycapText(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower switch
        {
            "space" => "Space",
            "enter" => "Enter",
            "return" => "Return",
            "escape" => "Esc",
            "backspace" => "Backspace",
            "tab" => "Tab",
            "left_arrow" => "←",
            "right_arrow" => "→",
            "up_arrow" => "↑",
            "down_arrow" => "↓",
            _ when lower.Length == 1 && char.IsLetterOrDigit(lower[0]) => lower.ToUpperInvariant(),
            _ when lower.StartsWith('f')
                && int.TryParse(lower.AsSpan(1), out _) => lower.ToUpperInvariant(),
            _ => TitleWords(lower),
        };
    }

    static string TitleWords(string value) => string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    static string Humanize(string token)
    {
        if (token.Length == 0) return "Output";
        var text = token.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
