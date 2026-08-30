using Avalonia;

namespace QuadStick.App;

// Which QuadStick the profile is being written for. Position is the picker
// order and the saved settings value, so do not reorder.
public enum QsModel { FPS, Original, Singleton }

// One part on one model's photo.
//
// PointX and PointY are fractions of the whole image file, which is how they
// were measured, so re-exporting the photo at another resolution keeps the
// marker on the same hole. LabelX is a stage coordinate: callouts sit in the
// clear bands above and below the photo, never on the device, because six
// labels dropped onto the parts cover the parts and each other.
readonly record struct Hotspot(string Zone, double LabelX, bool Bottom, double PointX, double PointY);

// The five mode lights across the top of the case, as fractions of the image.
readonly record struct ModeLightRow(double X, double Gap, double Y);

// Everything the device view needs to draw one model: its picture, the part of
// that picture worth showing, where each part sits on it, and which parts the
// model physically has.
//
// This exists so the three models cannot share a photo or a hotspot by
// accident. Before it, the FPS photo and one hardcoded hotspot array were drawn
// for every model, so an Original owner was shown an FPS and a Singleton owner
// was shown holes their device does not have.
sealed record DeviceDiagram(
    QsModel Model,
    string Asset,
    PixelSize Native,
    // The sub-rectangle of the image that holds the device, in fractions.
    // The catalog exports are square with wide transparent margins; showing
    // the whole file would draw the device at half the size it needs to be.
    Rect Source,
    double PhotoX,
    double PhotoY,
    double PhotoW,
    Hotspot[] Hotspots,
    ModeLightRow? Lights,
    string[] Zones)
{
    // Derived, never typed: the source crop and the image's own aspect decide
    // it, so the photo cannot be stretched by a mistyped number.
    public double PhotoH =>
        PhotoW / Source.Width * Source.Height * Native.Height / Native.Width;

    public Rect Photo => new(PhotoX, PhotoY, PhotoW, PhotoH);

    // The photo drawn at full size behind the crop window, and where its top
    // left corner goes. The Image is laid out at this size inside a clipped
    // frame, which crops without touching the asset or asking the headless
    // renderer to decode a sub-rectangle.
    public Size FullSize => new(PhotoW / Source.Width, PhotoH / Source.Height);
    public Point FullOffset => new(-Source.X * FullSize.Width, -Source.Y * FullSize.Height);

    // An image fraction turned into a point inside the photo box.
    public Point OnPhoto(double fx, double fy) => new(
        (fx - Source.X) / Source.Width * PhotoW,
        (fy - Source.Y) / Source.Height * PhotoH);

    public bool HasZone(string zoneId) => Array.IndexOf(Zones, zoneId) >= 0;

    public static DeviceDiagram For(QsModel model) => model switch
    {
        QsModel.Original => OriginalDiagram,
        QsModel.Singleton => SingletonDiagram,
        _ => FpsDiagram,
    };

    // Parts an FPS and an Original both have. The two differ in joystick
    // precision, not in what can be mapped, so they carry the same zones and
    // differ only in photo and geometry.
    static readonly string[] FullZones =
        { "joystick", "mp_left", "mp_center", "mp_right", "combo", "side", "lip", "jacks", "other" };

    // The Singleton has one mouthpiece tube and a joystick. No left or right
    // hole means no hole combos and no side tube, and it carries neither a lip
    // switch nor switch jacks.
    static readonly string[] SingletonZones = { "joystick", "mp_center", "other" };

    // Every number below was measured off the asset named beside it, as a
    // fraction of that file. Replacing a photo means measuring them again;
    // DeviceHotspotTests pins each file's pixel size so a swap fails loudly
    // instead of quietly pointing at the wrong hole.
    static readonly PixelSize Catalog2048 = new(2048, 2048);

    // The whole file, for a photo already framed on the device.
    static readonly Rect Whole = new(0, 0, 1, 1);

    // The FPS photo is framed on the device already, so it is shown whole, and
    // its hotspots are the ones measured on it before this layer existed: the
    // same points, written as fractions instead of stage coordinates.
    static readonly DeviceDiagram FpsDiagram = new(
        QsModel.FPS,
        "avares://QuadStickConfigManager/Assets/QuadStickFPS.png",
        new PixelSize(1536, 1024),
        Source: Whole,
        PhotoX: 130, PhotoY: 132, PhotoW: 440,
        Hotspots: new[]
        {
            new Hotspot("mp_left", 10, false, 0.3136, 0.4778),
            new Hotspot("mp_center", 180, false, 0.4386, 0.4778),
            new Hotspot("mp_right", 350, false, 0.5614, 0.4778),
            new Hotspot("side", 520, false, 0.7432, 0.4710),   // the bore of the side tube, not its body
            new Hotspot("joystick", 185, true, 0.3114, 0.5768), // the left arch of the gimbal, clear of the lip disc
            new Hotspot("lip", 355, true, 0.4295, 0.6894),
        },
        // The domes' paired specular glints give the centre of each, and a line
        // fitted through the five gives the spacing. None is lit in the photo;
        // the app draws its own.
        Lights: new ModeLightRow(0.2407, 0.1000, 0.1729),
        Zones: FullZones);

    // The Original is the FPS's picture and the FPS's callouts, on purpose.
    // The two take the same inputs (FW 2373 has no occurrence of either model
    // name, and input_keywords.h is one flat table with no model dimension),
    // and they carry the same parts in the same places. The difference is
    // joystick precision, which is not something a diagram can show. Giving
    // the Original a second photo of the same layout bought nothing and cost
    // a worse photo.
    static readonly DeviceDiagram OriginalDiagram = FpsDiagram with { Model = QsModel.Original };

    // Two parts, so two callouts, side by side in the top band. Nothing is
    // pinned below, so the whole lower half of the stage goes to the photo:
    // a Singleton drawn at the width a six-callout model needs is a postage
    // stamp with two labels floating around it.
    static readonly DeviceDiagram SingletonDiagram = new(
        QsModel.Singleton,
        "avares://QuadStickConfigManager/Assets/QuadStickSingleton.png",
        Catalog2048,
        Source: new Rect(0.2124, 0.1738, 0.5747, 0.6523),
        PhotoX: 140, PhotoY: 132, PhotoW: 420,
        Hotspots: new[]
        {
            // The left arch of the gimbal, not its centre: the mouthpiece tube
            // comes straight down out of the middle, so a marker there and the
            // one on the mouthpiece would swap sides and cross their leaders.
            new Hotspot("joystick", 40, false, 0.375, 0.500),
            new Hotspot("mp_center", 500, false, 0.480, 0.715),  // the mouthpiece on the end of the tube
        },
        Lights: new ModeLightRow(0.332, 0.0586, 0.239),
        Zones: SingletonZones);

    internal static readonly DeviceDiagram[] All = { FpsDiagram, OriginalDiagram, SingletonDiagram };
}
