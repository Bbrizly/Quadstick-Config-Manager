using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

public class OutputVisualTests
{
    [Fact]
    public void Every_known_output_has_a_readable_visual_description()
    {
        foreach (var token in Vocab.KnownOutputs)
        {
            var visual = OutputVisuals.For(token);
            Assert.Equal(token, visual.Token);
            Assert.False(string.IsNullOrWhiteSpace(visual.FriendlyLabel), token);
        }
    }

    [Fact]
    public void Unknown_custom_output_gets_a_readable_fallback()
    {
        var visual = OutputVisuals.For("vendor_custom_action");

        Assert.True(visual.IsFallback);
        Assert.Equal(OutputVisualKind.Generic, visual.Kind);
        Assert.Equal("Vendor custom action", visual.FriendlyLabel);
        Assert.NotNull(OutputVisuals.Render(visual));
    }

    [Theory]
    [InlineData("dpad_N", OutputDirection.N)]
    [InlineData("dpad_NE", OutputDirection.NE)]
    [InlineData("dpad_E", OutputDirection.E)]
    [InlineData("dpad_SE", OutputDirection.SE)]
    [InlineData("dpad_S", OutputDirection.S)]
    [InlineData("dpad_SW", OutputDirection.SW)]
    [InlineData("dpad_W", OutputDirection.W)]
    [InlineData("dpad_NW", OutputDirection.NW)]
    public void Every_dpad_direction_uses_the_same_parameterized_visual(string token, OutputDirection direction)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(OutputVisualKind.DPad, visual.Kind);
        Assert.Equal(direction, visual.Direction);
        Assert.NotNull(OutputVisuals.Render(visual));
    }

    [Theory]
    [InlineData("kb_a", "A")]
    [InlineData("kb_f12", "F12")]
    [InlineData("kb_space", "Space")]
    public void Keyboard_tokens_generate_keycaps(string token, string keycap)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(OutputVisualKind.KeyboardKeycap, visual.Kind);
        Assert.Equal(keycap, visual.KeycapText);
        Assert.Equal(keycap, visual.FriendlyLabel);
    }

    // Mouse prompts were bundled with the controller and key artwork, but
    // mouse outputs quietly fell all the way through to a text-only fallback.
    // Keep the firmware tokens tied to their actual highlighted prompt files.
    [Theory]
    [InlineData("mouse_left_button", "mouse:0069")]
    [InlineData("mouse_right_button", "mouse:0070")]
    [InlineData("mouse_middle_button", "mouse:0071")]
    [InlineData("mouse_wheel_up", "mouse:0072")]
    [InlineData("mouse_wheel_down", "mouse:0073")]
    [InlineData("mouse_back", "mouse:0075")]
    [InlineData("mouse_forward", "mouse:0076")]
    [InlineData("mouse_left", "mouse:0068")]
    [InlineData("mouse_pan_right", "mouse:0068")]
    public void Mouse_outputs_use_the_bundled_mouse_prompt(string token, string assetKey)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(assetKey, visual.AssetKey);
        Assert.NotNull(OutputVisuals.AssetPath(assetKey));
    }

    [Theory]
    [InlineData("x", ControllerFaceButton.X)]
    [InlineData("circle", ControllerFaceButton.Circle)]
    [InlineData("square", ControllerFaceButton.Square)]
    [InlineData("triangle", ControllerFaceButton.Triangle)]
    [InlineData("A", ControllerFaceButton.A)]
    [InlineData("B", ControllerFaceButton.B)]
    [InlineData("X", ControllerFaceButton.X)]
    [InlineData("Y", ControllerFaceButton.Y)]
    public void Face_buttons_use_the_face_button_visual(string token, ControllerFaceButton button)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(OutputVisualKind.FaceButton, visual.Kind);
        Assert.Equal(button, visual.FaceButton);
        Assert.NotNull(visual.Symbol);
    }

    [Fact]
    public void Face_button_visual_follows_the_existing_xbox_label_policy()
    {
        var visual = OutputVisuals.For("x", _ => "A button");

        Assert.Equal("A button", visual.FriendlyLabel);
        Assert.Equal("A", visual.Symbol);
        Assert.Equal(ControllerPromptStyle.Xbox, visual.PromptStyle);
        Assert.Equal("xbox:a", visual.AssetKey);
    }

    [Fact]
    public void Prompt_style_selects_authentic_platform_art_without_changing_token()
    {
        var playstation = OutputVisuals.For("circle", null, ControllerPromptStyle.Playstation);
        var xbox = OutputVisuals.For("circle", _ => "B button", ControllerPromptStyle.Xbox);

        Assert.Equal("circle", playstation.Token);
        Assert.Equal("ps:circle", playstation.AssetKey);
        Assert.Equal("circle", xbox.Token);
        Assert.Equal("xbox:b", xbox.AssetKey);
    }

    [Theory]
    [InlineData("left_joy_up", ControllerStickSide.Left, OutputDirection.N, false)]
    [InlineData("right_joy_down", ControllerStickSide.Right, OutputDirection.S, false)]
    [InlineData("left_stick", ControllerStickSide.Left, null, true)]
    public void Stick_outputs_use_the_same_moving_stick_visual_family(
        string token, ControllerStickSide side, OutputDirection? direction, bool isStickClick)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(OutputVisualKind.Joystick, visual.Kind);
        Assert.Equal(side, visual.StickSide);
        Assert.Equal(direction, visual.Direction);
        Assert.Equal(isStickClick, visual.IsStickClick);
        Assert.Equal(isStickClick, visual.AssetKey is not null);
        Assert.NotNull(OutputVisuals.Render(visual));
    }

    [Fact]
    public void Rendered_visuals_keep_the_friendly_accessible_name()
    {
        var rendered = OutputVisuals.Render(OutputVisuals.For("circle"));

        Assert.Contains("Circle", AutomationProperties.GetName(rendered));
        Assert.Contains("Circle", string.Join(" ", rendered.GetVisualDescendants()
            .Select(AutomationProperties.GetName).Where(name => !string.IsNullOrWhiteSpace(name))));
    }

    [Fact]
    public void Self_describing_outputs_do_not_repeat_their_label_beside_the_visual()
    {
        foreach (var token in new[] { "circle", "dpad_NE", "kb_a", "mouse_left_button" })
        {
            var visual = OutputVisuals.For(token);
            var rendered = Assert.IsType<Grid>(OutputVisuals.Render(visual));

            Assert.True(visual.IsSelfDescribing, token);
            Assert.Single(rendered.Children);
            Assert.Equal(visual.AccessibleName, AutomationProperties.GetName(rendered));
        }
    }

    // A firmware idea is not a button, so it gets no picture at all. The
    // neutral box that used to sit beside these words drew a control the
    // QuadStick does not have.
    [Theory]
    [InlineData("decrement_mode", "Decrement mode")]
    [InlineData("touch", "Touch")]
    [InlineData("vendor_custom_action", "Vendor custom action")]
    public void Abstract_outputs_are_words_with_no_artwork(string token, string label)
    {
        var visual = OutputVisuals.For(token);
        var rendered = Assert.IsType<TextBlock>(OutputVisuals.Render(visual));

        Assert.False(visual.IsSelfDescribing);
        Assert.Equal(label, rendered.Text);
        Assert.Equal(label, AutomationProperties.GetName(rendered));
        Assert.Empty(rendered.GetVisualDescendants().OfType<Image>());
        // Nothing to hide behind includeLabel: false either.
        Assert.IsType<TextBlock>(OutputVisuals.Render(visual, includeLabel: false));
    }

    [AvaloniaFact]
    public void Core_visuals_render_with_embedded_xelu_art()
    {
        foreach (var visual in new[]
        {
            OutputVisuals.For("circle"), OutputVisuals.For("dpad_N"),
            OutputVisuals.For("kb_a"), OutputVisuals.For("left_joy_up"),
        })
        {
            var viewbox = Assert.IsType<Viewbox>(Unwrap(OutputVisuals.Render(visual,
                includeLabel: false)));
            Assert.True(viewbox.Child is Image
                || viewbox.Child is Canvas canvas && canvas.Children.Count > 0, visual.Token);
            if (visual.Kind == OutputVisualKind.Joystick)
                Assert.DoesNotContain(((Canvas)viewbox.Child!).Children, child => child is Image);
        }
    }

    [AvaloniaFact]
    public void Stick_click_keeps_the_authentic_press_prompt_but_movement_does_not()
    {
        var click = Assert.IsType<Viewbox>(Unwrap(OutputVisuals.Render(
            OutputVisuals.For("left_stick"), includeLabel: false)));
        var movement = Assert.IsType<Viewbox>(Unwrap(OutputVisuals.Render(
            OutputVisuals.For("left_joy_up"), includeLabel: false)));

        Assert.IsType<Image>(click.Child);
        Assert.IsType<Canvas>(movement.Child);
        Assert.DoesNotContain(((Canvas)movement.Child).Children, child => child is Image);
    }

    // Matching the first letter of the label put an X button under circle,
    // square and triangle for every reader whose language does not spell the
    // B button with a B.
    [Theory]
    [InlineData("x", "ps:cross", "xbox:a")]
    [InlineData("circle", "ps:circle", "xbox:b")]
    [InlineData("square", "ps:square", "xbox:x")]
    [InlineData("triangle", "ps:triangle", "xbox:y")]
    public void Face_button_art_comes_off_the_token_not_the_translated_label(
        string token, string playstation, string xbox)
    {
        foreach (var label in new Func<string, string>?[] { null, _ => "\u4e38\u30dc\u30bf\u30f3" })
        {
            Assert.Equal(playstation,
                OutputVisuals.For(token, label, ControllerPromptStyle.Playstation).AssetKey);
            Assert.Equal(xbox, OutputVisuals.For(token, label, ControllerPromptStyle.Xbox).AssetKey);
        }
    }

    [AvaloniaFact]
    public void Every_prompt_asset_key_names_a_file_that_is_really_there()
    {
        // A key with no file behind it is silent: Asset() swallows it and the
        // drawn stand-in takes its place, which is how the whole Xbox d-pad
        // set shipped reading two files off and missing south and west.
        foreach (var style in new[] { ControllerPromptStyle.Playstation, ControllerPromptStyle.Xbox })
            foreach (var token in Vocab.KnownOutputs)
            {
                var key = OutputVisuals.For(token, null, style).AssetKey;
                if (key is null) continue;
                var art = OutputVisuals.AssetPath(key);
                Assert.True(art is not null, $"{token} -> {key} has no file");
                Assert.True(AssetLoader.Exists(new Uri(
                    $"avares://QuadStickConfigManager/Assets/OutputVisuals/{art!.Value.Path}")),
                    $"{token} -> {key} -> {art.Value.Path} is not embedded");
            }
    }

    [Theory]
    [InlineData("dpad_NE", ControllerPromptStyle.Playstation, "ps:dpad")]
    [InlineData("dpad_SW", ControllerPromptStyle.Xbox, "xbox:dpad")]
    [InlineData("dpad_S", ControllerPromptStyle.Xbox, "xbox:dpad-s")]
    [InlineData("dpad_W", ControllerPromptStyle.Xbox, "xbox:dpad-w")]
    public void Diagonals_and_every_cardinal_keep_the_authentic_pad(
        string token, ControllerPromptStyle style, string assetKey)
    {
        Assert.Equal(assetKey, OutputVisuals.For(token, null, style).AssetKey);
    }

    [AvaloniaFact]
    public void Every_dpad_direction_draws_over_the_photographed_pad()
    {
        foreach (var style in new[] { ControllerPromptStyle.Playstation, ControllerPromptStyle.Xbox })
            foreach (var token in new[]
            {
                "dpad_N", "dpad_NE", "dpad_E", "dpad_SE",
                "dpad_S", "dpad_SW", "dpad_W", "dpad_NW",
            })
            {
                var rendered = Assert.IsType<Viewbox>(OutputVisuals.Render(
                    OutputVisuals.For(token, null, style), includeLabel: false));
                var art = rendered.Child is Image
                    || rendered.Child is Canvas canvas && canvas.Children.OfType<Image>().Any();
                Assert.True(art, $"{style} {token} fell back to the drawn pad");
            }
    }

    // Replaces an earlier rule that kept "Left Stick down" beside the drawing.
    // The words were the only thing saying which stick and which way, and in
    // the 30% detail panel they ran off the edge of the card. The drawing says
    // both now: a letter beside the well, a wedge on the rim.
    [AvaloniaFact]
    public void Moving_a_stick_draws_its_side_and_its_direction_without_words()
    {
        var visual = OutputVisuals.For("left_joy_down");
        var rendered = OutputVisuals.Render(visual);

        Assert.False(visual.RequiresTextLabel);
        Assert.DoesNotContain(rendered.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == visual.FriendlyLabel);
        Assert.Contains(rendered.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "L");

        // Opposite directions cannot draw the same wedge.
        static IList<Avalonia.Point> Wedge(string token) =>
            OutputVisuals.Render(OutputVisuals.For(token))
            .GetVisualDescendants().OfType<Canvas>()
            .SelectMany(c => c.Children).OfType<Polygon>().Single().Points;
        Assert.NotEqual(Wedge("left_joy_down"), Wedge("left_joy_up"));
    }

    [AvaloniaFact]
    public void Xelu_asset_uri_is_embedded_in_the_desktop_assembly()
    {
        using var stream = AssetLoader.Open(new Uri(
            "avares://QuadStickConfigManager/Assets/OutputVisuals/Playstation/Playstation0021.png"));
        Assert.True(stream.Length > 0);
    }

    [AvaloniaFact]
    public void Picker_selection_keeps_the_original_raw_output_token()
    {
        var settings = Settings.Load();
        var previousGrouping = settings.PickerGrouping;
        settings.TutorialSeen = true;
        settings.RememberWindow = false;
        settings.PickerGrouping = "Flat";
        Settings.Save(settings);

        try
        {
            var file = ProfileFile.Load(
                "Profile Name,,Solo\n" +
                "game.csv\n" +
                "Outputs,Function,usb\n" +
                "circle,normal,lip\n");
            var window = new MainWindow();
            window.Show();
            window.LoadProfile(file);
            window.SetDeviceViewForPreview(false);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var output = window.GetVisualDescendants().OfType<Button>()
                .First(b => (AutomationProperties.GetName(b) ?? "").StartsWith("Output for row 4"));
            output.Flyout!.ShowAt(output);
            Dispatcher.UIThread.RunJobs();
            var item = ((Control)((Flyout)output.Flyout).Content!).GetVisualDescendants().OfType<Button>()
                .First(b => AutomationProperties.GetName(b) == "circle");
            Ui.Click(item);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("circle", file.Document.Sheets[0].Bindings[0].Output);
            file.Dirty = false;
            window.Close();
        }
        finally
        {
            settings.PickerGrouping = previousGrouping;
            Settings.Save(settings);
        }
    }

    // The shoulder row was the last family with no picture at all: every
    // profile in the corpus binds R1 or R2, and both read as bare words.
    [Theory]
    [InlineData("left_1", null, "L1", false)]
    [InlineData("right_1", null, "R1", false)]
    [InlineData("left_2", null, "L2", true)]
    [InlineData("right_2", null, "R2", true)]
    [InlineData("left_1", ControllerPromptStyle.Xbox, "LB", false)]
    [InlineData("right_1", ControllerPromptStyle.Xbox, "RB", false)]
    [InlineData("right_2", ControllerPromptStyle.Xbox, "RT", true)]
    // The Xbox output vocabulary spells the same four buttons differently and
    // carries its own marking with no style set.
    [InlineData("left_bumper", null, "LB", false)]
    [InlineData("right_bumper", null, "RB", false)]
    [InlineData("left_trigger", null, "LT", true)]
    [InlineData("right_trigger", null, "RT", true)]
    [InlineData("right_trigger", ControllerPromptStyle.Playstation, "R2", true)]
    public void Bumpers_and_triggers_draw_their_hardware_marking(
        string token, ControllerPromptStyle? style, string marking, bool isTrigger)
    {
        var visual = OutputVisuals.For(token, null, style);

        Assert.Equal(OutputVisualKind.Shoulder, visual.Kind);
        Assert.True(visual.IsSelfDescribing, token);
        Assert.Equal(marking, visual.Symbol);
        Assert.Equal(isTrigger, visual.IsTrigger);
        Assert.Equal(token, visual.Token);
    }

    // The marking is moulded on the plastic, so it says the same thing in a
    // language that does not spell the right trigger with an R.
    [Fact]
    public void Shoulder_marking_comes_off_the_token_not_the_translated_label()
    {
        var visual = OutputVisuals.For("right_2", _ => "\u53f3\u30c8\u30ea\u30ac\u30fc");

        Assert.Equal("R2", visual.Symbol);
        Assert.Equal("\u53f3\u30c8\u30ea\u30ac\u30fc", visual.FriendlyLabel);
    }

    [AvaloniaFact]
    public void A_bumper_and_a_trigger_are_not_the_same_outline()
    {
        static Border Body(string token) => Assert.IsType<Grid>(OutputVisuals.Render(
                OutputVisuals.For(token), includeLabel: false))
            .GetVisualDescendants().OfType<Border>().First();

        var bumper = Body("right_1");
        var trigger = Body("right_2");

        // Nothing here is signalled by colour: the bar is short and fully
        // rounded, the paddle is deep and rounded only at the top.
        Assert.True(bumper.Height < trigger.Height);
        Assert.NotEqual(bumper.CornerRadius.BottomLeft, trigger.CornerRadius.BottomLeft);
        Assert.Equal("R1", bumper.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Equal("R2", trigger.GetVisualDescendants().OfType<TextBlock>().Single().Text);
    }

    // The press prompt was on disk and reachable only through the Xbox
    // vocabulary, so no PS3 profile ever drew it.
    [Theory]
    [InlineData("left_3", ControllerStickSide.Left, "ps:left-stick")]
    [InlineData("right_3", ControllerStickSide.Right, "ps:right-stick")]
    public void Pressing_the_stick_uses_the_press_prompt_under_both_vocabularies(
        string token, ControllerStickSide side, string assetKey)
    {
        var visual = OutputVisuals.For(token);

        Assert.Equal(OutputVisualKind.Joystick, visual.Kind);
        Assert.True(visual.IsStickClick, token);
        Assert.Equal(side, visual.StickSide);
        Assert.Equal(assetKey, visual.AssetKey);
        Assert.NotNull(OutputVisuals.AssetPath(assetKey));
    }

    // Every stick visual is the drawing plus the letter that says which stick
    // it is, so a test that wants the drawing has to step past the letter.
    static Control Unwrap(Control rendered) =>
        rendered is StackPanel row ? (Control)row.Children[^1] : rendered;

    [AvaloniaFact]
    public void The_two_sticks_do_not_draw_the_same_picture()
    {
        static string Letter(string token) => Assert.IsType<StackPanel>(
                OutputVisuals.Render(OutputVisuals.For(token), includeLabel: false))
            .GetLogicalDescendants().OfType<TextBlock>().Single().Text ?? "";

        // Xelu ships one stick picture for both, and a press prompt carries no
        // words of its own, so without the letter these two were pixel for
        // pixel the same picture. A moving stick keeps its side in the words
        // beside it instead.
        Assert.Equal("L", Letter("left_stick"));
        Assert.Equal("R", Letter("right_stick"));
    }
}
