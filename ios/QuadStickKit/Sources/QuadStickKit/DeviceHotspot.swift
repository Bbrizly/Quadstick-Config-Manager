import Foundation

/// Which physical QuadStick a profile is being edited for. Position is the
/// picker order and the value saved in settings, so do not reorder it.
public enum QuadStickModel: String, Codable, CaseIterable, Sendable {
    case fps, original, singleton

    public var displayName: String {
        switch self {
        case .fps: "QuadStick FPS"
        case .original: "QuadStick Original"
        case .singleton: "QuadStick Singleton"
        }
    }

    public var summary: String {
        switch self {
        case .fps:
            "FPS: 3-hole mouthpiece, side tube, lip sensor, rear jacks. More precise joystick than the Original."
        case .original:
            "Original: 3-hole mouthpiece, side tube, lip switch, rear jacks. Same inputs as the FPS."
        case .singleton:
            "Singleton: a single sip/puff tube on the joystick. Uses sip and puff patterns plus joystick movement."
        }
    }
}

/// One tappable part on one model's photo.
///
/// x and y are fractions of the whole image file, exactly as measured on the
/// desktop app's own diagram (DeviceDiagram.cs), not of the cropped photo
/// box that gets drawn. That keeps a hotspot pinned to the same hole if the
/// crop is ever adjusted without a re-measure.
public struct DeviceHotspot: Identifiable, Hashable, Sendable {
    /// The catalog input this region opens.
    public let inputID: String
    public let x: Double
    public let y: Double
    /// What to write under the ring. The full name runs into its neighbour
    /// at three-tube spacing, so this is the short form.
    public let shortName: String

    public init(inputID: String, x: Double, y: Double, shortName: String) {
        self.inputID = inputID
        self.x = x
        self.y = y
        self.shortName = shortName
    }

    public var id: String { inputID }
}

/// The five mode lights across the top of the case, as fractions of the
/// image file. Most photos are regular enough for x + gap; the Singleton's
/// camera angle makes the visible centers vary by several pixels, so it
/// carries its own measured positions instead.
public struct ModeLightRow: Hashable, Sendable {
    public let x: Double
    public let gap: Double
    public let y: Double
    public let points: [Double]?

    public init(x: Double, gap: Double, y: Double, points: [Double]? = nil) {
        self.x = x
        self.gap = gap
        self.y = y
        self.points = points
    }

    public func xAt(_ index: Int) -> Double {
        if let points, !points.isEmpty { return points[index] }
        return x + Double(index) * gap
    }
}

/// Everything the device view needs to draw one model: its picture, the crop
/// of that picture worth showing, where each part sits on it, and its mode
/// lights. One value per model, so the three can never share a photo or a
/// hotspot by accident, the way a single shared table once showed an
/// Original owner an FPS photo and a Singleton owner three holes they do
/// not have.
public struct DevicePhoto: Sendable {
    public let model: QuadStickModel
    public let assetName: String
    public let nativeWidth: Int
    public let nativeHeight: Int
    // The sub-rectangle of the file that holds the device, in fractions.
    // FPS and Original are already framed on the device; the Singleton's
    // catalog export has wide transparent margins that would otherwise
    // shrink it to half size.
    public let sourceX: Double
    public let sourceY: Double
    public let sourceWidth: Double
    public let sourceHeight: Double
    public let hotspots: [DeviceHotspot]
    public let lights: ModeLightRow

    public init(model: QuadStickModel, assetName: String, nativeWidth: Int, nativeHeight: Int,
                sourceX: Double = 0, sourceY: Double = 0, sourceWidth: Double = 1, sourceHeight: Double = 1,
                hotspots: [DeviceHotspot], lights: ModeLightRow) {
        self.model = model
        self.assetName = assetName
        self.nativeWidth = nativeWidth
        self.nativeHeight = nativeHeight
        self.sourceX = sourceX
        self.sourceY = sourceY
        self.sourceWidth = sourceWidth
        self.sourceHeight = sourceHeight
        self.hotspots = hotspots
        self.lights = lights
    }

    /// Width over height of the photo as it is actually drawn, i.e. of the
    /// crop, not of the raw file. The Singleton's crop is taller than its
    /// own file.
    public var aspectRatio: Double {
        (sourceWidth / sourceHeight) * (Double(nativeWidth) / Double(nativeHeight))
    }

    /// A hotspot's position translated from "fraction of the file" (how it
    /// was measured) to "fraction of the cropped photo" (how it is drawn).
    public func position(of spot: DeviceHotspot) -> (x: Double, y: Double) {
        ((spot.x - sourceX) / sourceWidth, (spot.y - sourceY) / sourceHeight)
    }

    /// How far apart the two closest regions are, as a fraction of the drawn
    /// photo's width. A ring drawn wider than this overlaps its neighbour,
    /// which puts one part's tap target on top of another's.
    public var minimumSeparation: Double {
        let points = hotspots.map(position)
        var closest = Double.greatestFiniteMagnitude
        for i in points.indices {
            for j in points.indices.dropFirst(i + 1) {
                // Both axes in units of the drawn photo's width, so its
                // aspect ratio does not make vertical gaps look bigger than
                // they are.
                let dx = points[i].x - points[j].x
                let dy = (points[i].y - points[j].y) / aspectRatio
                closest = min(closest, (dx * dx + dy * dy).squareRoot())
            }
        }
        return closest
    }

    public static func `for`(_ model: QuadStickModel) -> DevicePhoto {
        switch model {
        case .fps: fps
        case .original: original
        case .singleton: singleton
        }
    }

    // Every number below was measured off the file named beside it, as a
    // fraction of that file. Replacing a photo means measuring again;
    // DeviceModelTests pins each file's pixel size so a swap fails loudly
    // instead of quietly pointing at the wrong part.

    // The FPS photo is framed on the device already, so it is shown whole.
    // Points are the centers of the three mouthpiece bores, the side-tube
    // bore, and the lip sensor visible in this photo.
    static let fps = DevicePhoto(
        model: .fps,
        assetName: "QuadStickFPS",
        nativeWidth: 1536, nativeHeight: 1024,
        hotspots: [
            DeviceHotspot(inputID: "left-tube", x: 0.3841, y: 0.5440, shortName: "Left"),
            DeviceHotspot(inputID: "center-tube", x: 0.4824, y: 0.5440, shortName: "Center"),
            DeviceHotspot(inputID: "right-tube", x: 0.5801, y: 0.5440, shortName: "Right"),
            // The bore of the side tube, not its body.
            DeviceHotspot(inputID: "side-tube", x: 0.7266, y: 0.5450, shortName: "Side"),
            // The gimbal's left centring tick, the same landmark the
            // Singleton uses for its own joystick point. The desktop can aim
            // at the mouthpiece's lower left edge instead, because a leader
            // line carries its label away to clear space; here the ring is
            // the tap target itself, so it has to sit on the joystick's own
            // body. This clears minimumSeparation from the left tube.
            DeviceHotspot(inputID: "joystick", x: 0.325, y: 0.425, shortName: "Joystick"),
            DeviceHotspot(inputID: "lip-switch", x: 0.4883, y: 0.6770, shortName: "Lip"),
        ],
        lights: ModeLightRow(x: 0.3275, gap: 0.0862, y: 0.1064))

    // The Original and FPS take the same inputs (FW 2373 has no occurrence
    // of either model name, and input_keywords.h is one flat table with no
    // model dimension), but they are distinct physical products shown on
    // their own photo.
    static let original = DevicePhoto(
        model: .original,
        assetName: "QuadStickOriginal",
        nativeWidth: 1536, nativeHeight: 1024,
        hotspots: [
            DeviceHotspot(inputID: "left-tube", x: 0.3136, y: 0.4778, shortName: "Left"),
            DeviceHotspot(inputID: "center-tube", x: 0.4386, y: 0.4778, shortName: "Center"),
            DeviceHotspot(inputID: "right-tube", x: 0.5614, y: 0.4778, shortName: "Right"),
            DeviceHotspot(inputID: "side-tube", x: 0.7432, y: 0.4710, shortName: "Side"),
            // Same reasoning as the FPS joystick point above: moved down the
            // gimbal arch so it clears the left tube hole instead of
            // sitting under it.
            DeviceHotspot(inputID: "joystick", x: 0.306, y: 0.625, shortName: "Joystick"),
            DeviceHotspot(inputID: "lip-switch", x: 0.4295, y: 0.6894, shortName: "Lip"),
        ],
        lights: ModeLightRow(x: 0.2407, gap: 0.1000, y: 0.1729))

    // Two parts, so the crop keeps the joystick and the tube. The Singleton
    // has one mouthpiece tube and a joystick: no left or right hole means no
    // side tube, no lip switch, no jacks.
    static let singleton = DevicePhoto(
        model: .singleton,
        assetName: "QuadStickSingleton",
        nativeWidth: 2048, nativeHeight: 2048,
        sourceX: 0.2124, sourceY: 0.1738, sourceWidth: 0.5747, sourceHeight: 0.6523,
        hotspots: [
            // The left arch of the gimbal, not its centre: the mouthpiece
            // tube comes straight down out of the middle, so a marker there
            // and the one on the mouthpiece would sit on top of each other.
            // Unlike the FPS and Original, this point is already far enough
            // from the tube's hotspot without adjustment.
            DeviceHotspot(inputID: "joystick", x: 0.375, y: 0.500, shortName: "Joystick"),
            // The Singleton has one tube, not three, so there is nothing to
            // call it the center of.
            DeviceHotspot(inputID: "center-tube", x: 0.480, y: 0.715, shortName: "Tube"),
        ],
        // Measured lens centers in the 2048px source: (690, 498), (815, 498),
        // (935, 498), (1048, 498), (1159, 498). They are not evenly spaced in
        // this photograph, so one x/gap pair would leave the outer lights
        // visibly off-center.
        lights: ModeLightRow(
            x: 690.0 / 2048, gap: 0, y: 498.0 / 2048,
            points: [690.0 / 2048, 815.0 / 2048, 935.0 / 2048, 1048.0 / 2048, 1159.0 / 2048]))
}
