using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using System.Buffers.Binary;
using System.IO;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using QuadStick.App;
using QuadStick.Format;
using Xunit;

namespace QuadStick.App.Tests;

// Device view is a photo of the device with one label per part and a leader
// line to the part it names. A label that covers another label, or that hangs
// off the picture, hides the part it is there to point at. Both went wrong the
// first time the labels were dropped straight onto the parts.
//
// There are three photos now, one per model, and the model decides which parts
// get a label at all. A Singleton owner shown an FPS with three holes on it is
// being told to sip on a hole their device has not got.
public class DeviceHotspotTests
{
    static MainWindow Open(QsModel model = QsModel.FPS, ProfileFile? profile = null)
    {
        var s = Settings.Load();
        s.TutorialSeen = true;
        s.RememberWindow = false;
        s.Model = model.ToString();
        Settings.Save(s);
        var w = new MainWindow();
        w.Show();
        var file = profile ?? ProfileFile.NewFromTemplate("mygame.csv");
        file.Dirty = false; // else Close opens the save dialog and waits forever
        w.LoadProfile(file);
        w.SetDeviceViewForPreview(true);
        w.UpdateLayout();
        return w;
    }

    // The stage is the one Canvas the callouts are laid out on. The photo sits
    // inside its own clipped window on that stage, so "the canvas with an
    // Image in it" is the window, not the stage.
    static Canvas Stage(MainWindow w)
    {
        var stage = w.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(c => c.Name == "DeviceStage");
        Assert.True(stage is not null, "the device stage is not in the visual tree");
        return stage!;
    }

    static Canvas PhotoFrame(MainWindow w) =>
        Stage(w).Children.OfType<Canvas>().Single(c => c.Name == "DevicePhotoFrame");

    static (string Name, Rect Box)[] Labels(MainWindow w) =>
        Stage(w).Children.OfType<ToggleButton>()
            // Bounds, not the Canvas.Left/Top pair: a top callout is pinned by
            // its bottom edge so it can grow upward, and Canvas.GetTop reads
            // NaN for those. Bounds is the rectangle it was actually given.
            .Select(b => (AutomationProperties.GetName(b) ?? "", b.Bounds))
            .ToArray();

    // The lit mode lights: the leader-line markers are ellipses too, but they
    // are drawn with a stroke and these are not.
    static double[] LitLights(MainWindow w) =>
        Stage(w).Children.OfType<Ellipse>()
            .Where(e => e.StrokeThickness == 0 && e.Opacity == 1)
            .Select(Canvas.GetLeft)
            .OrderBy(x => x)
            .ToArray();

    public static IEnumerable<object[]> Models =>
        EveryModel.Select(m => new object[] { m });

    static readonly QsModel[] EveryModel = { QsModel.FPS, QsModel.Original, QsModel.Singleton };

    // Mode 1 lights the leftmost of the five; mode 2 the one to its right.
    // Both patterns come from the firmware's own table, see ModeLightsTests.
    [AvaloniaFact]
    public void The_mode_lights_follow_the_mode()
    {
        var file = ProfileFile.NewFromTemplate("mygame.csv");
        int second = file.AddModeSheet("Driving");
        var w = Open(profile: file);
        try
        {
            var one = LitLights(w);
            Assert.Single(one);

            w.SelectSheetForPreview(second);
            w.UpdateLayout();

            var two = LitLights(w);
            Assert.Single(two);
            Assert.True(two[0] > one[0],
                $"mode 2's light should sit right of mode 1's: {two[0]} vs {one[0]}");
        }
        finally { w.Close(); }
    }

    // Every hotspot and mode-light number in DeviceDiagram is measured off one
    // photo, as a fraction of that file. Dropping in a differently framed
    // picture leaves the numbers pointing at the wrong holes, which is what
    // happened when the first photo was replaced. Pin each file's size so the
    // swap fails here instead of on screen.
    [AvaloniaTheory]
    [MemberData(nameof(Models))]
    public void Each_photo_is_the_one_its_hotspots_were_measured_on(QsModel model)
    {
        var d = DeviceDiagram.For(model);
        // Read the PNG header rather than decoding: the headless platform hands
        // back a 1x1 stub bitmap, so PixelSize would prove nothing here.
        using var stream = AssetLoader.Open(new Uri(d.Asset));
        var head = new byte[24];
        using (var all = new MemoryStream())
        {
            stream.CopyTo(all);
            all.Position = 0;
            Assert.Equal(24, all.Read(head, 0, 24));
        }
        int width = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20, 4));
        Assert.True((width, height) == (d.Native.Width, d.Native.Height),
            $"{d.Asset} is {width}x{height}, not {d.Native.Width}x{d.Native.Height}. If the photo "
          + "changed, measure its hotspots and mode lights off the new one again.");
    }

    // The FPS and the Original take the same inputs and carry the same parts in
    // the same places, so they share one picture on purpose. The Singleton does
    // not, and showing it either of theirs is the bug this layer exists to stop.
    [Fact]
    public void The_singleton_never_borrows_another_models_picture()
    {
        var fps = DeviceDiagram.For(QsModel.FPS);
        var original = DeviceDiagram.For(QsModel.Original);
        var singleton = DeviceDiagram.For(QsModel.Singleton);

        Assert.Equal(fps.Asset, original.Asset);
        Assert.Equal(fps.Hotspots, original.Hotspots);
        Assert.NotEqual(fps.Asset, singleton.Asset);
        Assert.EndsWith("QuadStickSingleton.png", singleton.Asset, StringComparison.Ordinal);
    }

    // The two that share a picture have to agree on every number on it, or one
    // of them is pointing at holes measured for the other.
    [Fact]
    public void The_shared_picture_is_shared_whole()
    {
        var fps = DeviceDiagram.For(QsModel.FPS);
        var original = DeviceDiagram.For(QsModel.Original);
        Assert.Equal(fps, original with { Model = QsModel.FPS });
    }

    [AvaloniaFact]
    public void The_photo_on_screen_is_the_selected_models()
    {
        foreach (var model in EveryModel)
        {
            var w = Open(model);
            try
            {
                var photo = PhotoFrame(w).Children.OfType<Image>().Single();
                var d = DeviceDiagram.For(model);
                // The bitmap itself is a headless stub, so the proof is the
                // layout: each model crops and places its photo differently.
                Assert.Equal(d.FullSize.Width, photo.Width, 3);
                Assert.Equal(d.FullSize.Height, photo.Height, 3);
                Assert.Equal(d.PhotoX, Canvas.GetLeft(PhotoFrame(w)), 3);
            }
            finally { w.Close(); }
        }
    }

    // The crop and the photo box have to agree with the file's own shape, or
    // Stretch.Fill squashes the device and every measured fraction slides.
    [AvaloniaTheory]
    [MemberData(nameof(Models))]
    public void The_crop_does_not_stretch_the_photo(QsModel model)
    {
        var d = DeviceDiagram.For(model);
        double aspect = d.FullSize.Width / d.FullSize.Height;
        Assert.Equal((double)d.Native.Width / d.Native.Height, aspect, 3);
    }

    [AvaloniaFact]
    public void Every_callout_names_a_part_the_model_has()
    {
        foreach (var model in EveryModel)
        {
            var d = DeviceDiagram.For(model);
            foreach (var spot in d.Hotspots)
                Assert.True(d.HasZone(spot.Zone),
                    $"{model} has a callout for {spot.Zone}, which is not one of its parts");

            var w = Open(model);
            try
            {
                var drawn = Stage(w).Children.OfType<ToggleButton>()
                    .Select(b => AutomationProperties.GetName(b) ?? "").ToArray();
                Assert.Equal(d.Hotspots.Length, drawn.Length);
            }
            finally { w.Close(); }
        }
    }

    // The Singleton has one mouthpiece tube and a joystick. Nothing on its
    // picture may point at a left hole, a right hole, a side tube or a lip
    // switch, because it has none of them.
    [AvaloniaFact]
    public void A_singleton_gets_no_callout_for_a_part_it_does_not_have()
    {
        var w = Open(QsModel.Singleton);
        try
        {
            var names = Labels(w).Select(l => l.Name).ToArray();
            foreach (var missing in new[]
            {
                Strings.Main_LeftMouthpieceHole, Strings.Main_RightMouthpieceHole,
                Strings.Main_SideTube, Strings.Main_LipSwitch,
            })
                Assert.DoesNotContain(names, n => n.StartsWith(missing, StringComparison.Ordinal));

            Assert.Contains(names, n => n.StartsWith(Strings.Main_Joystick, StringComparison.Ordinal));
            Assert.Contains(names, n => n.StartsWith(Strings.Main_CenterMouthpieceHole, StringComparison.Ordinal));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Every_part_on_a_three_hole_device_has_a_label_on_the_photo()
    {
        foreach (var model in new[] { QsModel.FPS, QsModel.Original })
        {
            var w = Open(model);
            try
            {
                var names = Labels(w).Select(l => l.Name).ToArray();
                foreach (var part in new[]
                {
                    Strings.Main_Joystick, Strings.Main_LeftMouthpieceHole,
                    Strings.Main_CenterMouthpieceHole, Strings.Main_RightMouthpieceHole,
                    Strings.Main_SideTube, Strings.Main_LipSwitch,
                })
                    Assert.True(names.Any(n => n.StartsWith(part, StringComparison.Ordinal)),
                        $"{part} has no label on the {model} photo");
            }
            finally { w.Close(); }
        }
    }

    // A profile written for an FPS keeps working on a Singleton: the rows are
    // still there, the cards are still reachable, and the app says so in words
    // rather than leaving somebody to notice a dimmed card.
    [AvaloniaFact]
    public void A_mapping_the_model_lacks_stays_editable_and_is_flagged()
    {
        var file = ProfileFile.NewFromTemplate("fps.csv");
        var sheet = file.Document.Sheets.First(s => s.Type == SheetType.ProfileName);
        // An unmapped row, so the profile ends up with exactly one binding on
        // the left hole rather than a two-input combo.
        int row = sheet.Bindings.First(b => b.Inputs.Count == 0).Row;
        file.SetCell(row, 2, "mp_left_sip");
        var w = Open(QsModel.Singleton, file);
        try
        {
            // Said in words over the picture, not left to a dimmed card.
            var texts = w.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? "").ToList();
            Assert.Contains(texts, t => t.Contains("QuadStick Singleton", StringComparison.Ordinal)
                                     && t.Contains("does not have", StringComparison.OrdinalIgnoreCase));

            // Still reachable: the left hole keeps a row behind the off-model
            // card, and the row itself is untouched in the file.
            Assert.Contains(OffModelRows(w).Select(b => AutomationProperties.GetName(b) ?? ""),
                n => n.StartsWith(Strings.Main_LeftMouthpieceHole, StringComparison.Ordinal));
            Assert.Equal("mp_left_sip", file.GetCell(row, 2));

            // And no marker was drawn for it on the picture.
            Assert.DoesNotContain(Labels(w), l => l.Name.StartsWith(Strings.Main_LeftMouthpieceHole, StringComparison.Ordinal));
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void No_label_covers_another_one()
    {
        foreach (var model in EveryModel)
        {
            var w = Open(model);
            try
            {
                var labels = Labels(w);
                for (int i = 0; i < labels.Length; i++)
                    for (int j = i + 1; j < labels.Length; j++)
                        Assert.False(labels[i].Box.Intersects(labels[j].Box),
                            $"on {model}, '{labels[i].Name}' and '{labels[j].Name}' overlap");
            }
            finally { w.Close(); }
        }
    }

    // A callout has to stay on the stage and off the device, at the scale a
    // low-vision reader actually uses as well as at the default one.
    [AvaloniaFact]
    public void No_label_leaves_the_stage_or_lands_on_the_device()
    {
        foreach (int scale in new[] { 100, 200 })
            foreach (var model in EveryModel)
            {
                var settings = Settings.Load();
                settings.InterfaceScalePercent = scale;
                Settings.Save(settings);
                var w = Open(model);
                w.ApplyInterfaceScale(scale);
                w.UpdateLayout();
                try
                {
                    var stage = Stage(w);
                    var frame = new Rect(0, 0, stage.Width, stage.Height);
                    var photoBox = PhotoFrame(w);
                    var photo = new Rect(Canvas.GetLeft(photoBox), Canvas.GetTop(photoBox),
                        photoBox.Width, photoBox.Height);
                    foreach (var (name, box) in Labels(w))
                    {
                        Assert.True(frame.Contains(box),
                            $"at {scale}% on {model}, '{name}' is outside the stage: {box} in {frame}");
                        Assert.True(box.Bottom <= photo.Top || box.Top >= photo.Bottom
                                 || box.Right <= photo.Left || box.Left >= photo.Right,
                            $"at {scale}% on {model}, '{name}' covers the device: {box} over {photo}");
                    }
                }
                finally
                {
                    w.ApplyInterfaceScale(100);
                    var restored = Settings.Load();
                    restored.InterfaceScalePercent = 100;
                    Settings.Save(restored);
                    w.Close();
                }
            }
    }

    // The width one word needs, in the type the callout draws it in. A wrapped
    // TextBlock asks for no more than the room it is given, so its DesiredSize
    // says nothing about a word that does not fit; the word has to be measured
    // on its own.
    static double WordWidth(TextBlock t, string word) =>
        new FormattedText(word, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight), t.FontSize, null).Width;

    // The gesture names are right aligned against the rule between the two
    // columns, so each one sits on the edge of its own box. A bold "f" inks
    // past the advance width the layout measures, and a box sized to that
    // measurement exactly shaved the tail off "Soft Puff" and "Puff". Word
    // widths alone miss it: every word fits, the line as a whole does not.
    [AvaloniaFact]
    public void A_callout_name_is_not_shaved_by_its_own_edge()
    {
        var w = Open();
        try
        {
            int checked_ = 0;
            foreach (var card in Stage(w).Children.OfType<ToggleButton>())
                foreach (var text in card.GetVisualDescendants().OfType<TextBlock>()
                             .Where(t => t.TextAlignment == TextAlignment.Right))
                {
                    double line = WordWidth(text, text.Text ?? "");
                    if (line > text.Bounds.Width) continue; // wrapped: the word check covers it
                    checked_++;
                    Assert.True(text.Bounds.Width - line >= 2,
                        $"'{text.Text}' is drawn in {text.Bounds.Width}px of the "
                        + $"{line}px it measures, so its last letter is cut");
                }
            Assert.True(checked_ > 0, "no callout names were measured");
        }
        finally { w.Close(); }
    }

    // A callout is a fixed width, so a word wider than its column is drawn
    // running off the edge and the card cuts it: "Decrement mode" read
    // "Decremen mode", which is not the name of anything. Xbox style is the
    // wordiest of the three ("View button", not "Select"), so it is the one
    // that has to fit.
    [AvaloniaFact]
    public void No_word_in_a_callout_is_cut_off()
    {
        var w = Open();
        try
        {
            w.CycleLabelStyleForPreview(); // plain English -> Xbox style
            w.UpdateLayout();
            foreach (var card in Stage(w).Children.OfType<ToggleButton>())
                foreach (var text in card.GetVisualDescendants().OfType<TextBlock>())
                    foreach (var word in (text.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        Assert.True(WordWidth(text, word) <= text.Bounds.Width + 0.5,
                            $"'{word}' needs {WordWidth(text, word)}px and the "
                            + $"{AutomationProperties.GetName(card)?.Split('.')[0]} callout gives it {text.Bounds.Width}px");
        }
        finally { w.Close(); }
    }

    // Every marker sits inside the photo it is pointing into. A fraction typed
    // outside 0..1, or measured on the wrong crop, lands the dot on the panel
    // beside the device instead of on the hole.
    [AvaloniaTheory]
    [MemberData(nameof(Models))]
    public void Every_marker_lands_inside_the_photo(QsModel model)
    {
        var d = DeviceDiagram.For(model);
        var box = new Rect(0, 0, d.PhotoW, d.PhotoH);
        foreach (var spot in d.Hotspots)
            Assert.True(box.Contains(d.OnPhoto(spot.PointX, spot.PointY)),
                $"{model}'s {spot.Zone} marker is off the photo");
        if (d.Lights is { } row)
            for (int i = 0; i < 5; i++)
                Assert.True(box.Contains(d.OnPhoto(row.X + i * row.Gap, row.Y)),
                    $"{model}'s mode light {i + 1} is off the photo");
    }

    // The one card in the side panel that stands for every part the model does
    // not have, and the rows it holds.
    static Button? OffModelCard(MainWindow w) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Flyout is not null && b.Classes.Contains("zone"));

    static ToggleButton[] OffModelRows(MainWindow w) =>
        OffModelCard(w)?.Flyout is Flyout f && f.Content is StackPanel body
            ? body.Children.OfType<ToggleButton>().ToArray()
            : Array.Empty<ToggleButton>();

    // Five dimmed rows for parts a Singleton has not got pushed the modes list
    // and the view keys off the side panel. They live behind one card now.
    [AvaloniaFact]
    public void Parts_the_model_lacks_take_one_row_between_them()
    {
        var w = Open(QsModel.Singleton, ForeignProfile());
        try
        {
            var panel = w.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "ZoneList");
            var names = panel.Children.OfType<Control>()
                .Select(c => AutomationProperties.GetName(c) ?? "").ToList();
            foreach (var missing in new[]
            {
                Strings.Main_LeftMouthpieceHole, Strings.Main_RightMouthpieceHole,
                Strings.Main_SideTube, Strings.Main_LipSwitch, Strings.Main_HoleCombos,
            })
                Assert.DoesNotContain(names, n => n.StartsWith(missing, StringComparison.Ordinal));

            var card = OffModelCard(w);
            Assert.True(card is not null, "the off-model parts have no card in the side panel");
            // Every one of them is still named and counted, one row deep.
            var rows = OffModelRows(w).Select(b => AutomationProperties.GetName(b) ?? "").ToList();
            Assert.Equal(5, rows.Count);
            foreach (var part in new[]
            {
                Strings.Main_LeftMouthpieceHole, Strings.Main_RightMouthpieceHole,
                Strings.Main_SideTube, Strings.Main_LipSwitch, Strings.Main_HoleCombos,
            })
                Assert.Contains(rows, n => n.StartsWith(part, StringComparison.Ordinal));
        }
        finally { w.Close(); }
    }

    // Picking one out of the card opens it in the editor, the same as picking a
    // part the device does have.
    [AvaloniaFact]
    public void A_part_behind_the_card_still_opens_in_the_editor()
    {
        var w = Open(QsModel.Singleton, ForeignProfile());
        try
        {
            var left = OffModelRows(w).First(b =>
                (AutomationProperties.GetName(b) ?? "").StartsWith(Strings.Main_LeftMouthpieceHole, StringComparison.Ordinal));
            Ui.Click(left);
            w.UpdateLayout();

            Assert.Equal("mp_left", w.SelectedZoneForPreview);
        }
        finally { w.Close(); }
    }

    // A profile that maps every part a Singleton has not got.
    static ProfileFile ForeignProfile()
    {
        var file = ProfileFile.NewFromTemplate("fps.csv");
        var sheet = file.Document.Sheets.First(s => s.Type == SheetType.ProfileName);
        var free = sheet.Bindings.Where(b => b.Inputs.Count == 0).Select(b => b.Row).ToList();
        foreach (var (input, i) in new[]
            { "mp_left_sip", "mp_right_sip", "right_sip", "lip", "mp_triple_sip" }.Select((x, i) => (x, i)))
            file.SetCell(free[i], 2, input);
        return file;
    }
}
