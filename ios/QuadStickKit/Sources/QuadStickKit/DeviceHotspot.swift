import Foundation

/// Where each part of the QuadStick sits on the device photo.
///
/// The numbers come from the desktop app, which measured them off
/// Assets/QuadStick.png at 1536x1024 (MainWindow.axaml.cs, `Hotspots`). They
/// are kept here as the same stage coordinates the desktop uses, divided down
/// to fractions, so the two apps can be compared without converting anything
/// by hand. Replacing the photo means measuring again in both places, and
/// KitTests pins the values so a swap fails loudly instead of quietly pointing
/// at the wrong hole.
public struct DeviceHotspot: Identifiable, Hashable, Sendable {
    /// The catalog input this region opens.
    public let inputID: String
    /// Position on the photo, 0...1 from its top left corner.
    public let x: Double
    public let y: Double
    /// What to write under the ring. The full name is on the row below the
    /// photo; here the three tube labels are 0.125 of the width apart and the
    /// full ones run into each other, so this is the short form.
    public let shortName: String

    public var id: String { inputID }
}

public enum DevicePhoto {

    /// The photo's own proportions. Layout scales the whole thing as one
    /// piece, so a region can never drift off the part it names.
    public static let aspectRatio: Double = 1536.0 / 1024.0

    // The desktop's stage: the photo occupies this rectangle, and the hotspot
    // points below are in the same space.
    private static let originX = 80.0, originY = 84.0, width = 440.0, height = 293.0

    private static func at(_ inputID: String, _ pointX: Double, _ pointY: Double,
                           _ shortName: String) -> DeviceHotspot {
        DeviceHotspot(inputID: inputID,
                      x: (pointX - originX) / width,
                      y: (pointY - originY) / height,
                      shortName: shortName)
    }

    /// Front face parts, in reading order across the device.
    public static let hotspots: [DeviceHotspot] = [
        at("left-tube", 218, 224, "Left"),
        at("center-tube", 273, 224, "Center"),
        at("right-tube", 327, 224, "Right"),
        at("side-tube", 407, 222, "Side"),      // the bore of the side tube, not its body
        // The desktop points at the top of the left gimbal arch, which sits
        // almost directly under the left tube hole. That is fine there, where
        // a leader line runs out to a label in clear space. Here the point is
        // the target, so it moves down the same arch until it clears the hole.
        // `minimumSeparation` is the rule this has to satisfy.
        DeviceHotspot(inputID: "joystick", x: 0.306, y: 0.625, shortName: "Joystick"),
        at("lip-switch", 269, 286, "Lip"),
    ]

    /// How far apart the two closest regions are, as a fraction of the photo's
    /// width. A ring drawn wider than this overlaps its neighbour, which puts
    /// one part's target on top of another part.
    public static var minimumSeparation: Double {
        var closest = Double.greatestFiniteMagnitude
        for (i, a) in hotspots.enumerated() {
            for b in hotspots.dropFirst(i + 1) {
                // Both axes in units of the photo's width, so the aspect ratio
                // does not make vertical gaps look bigger than they are.
                let dx = a.x - b.x
                let dy = (a.y - b.y) / aspectRatio
                closest = min(closest, (dx * dx + dy * dy).squareRoot())
            }
        }
        return closest
    }
}
