import Foundation

// Ported from src/QuadStick.App/OutputVisuals.cs. That file is the source of
// truth for the branch order and the traps recorded in its comments; this
// file carries the same decisions in Swift, minus the Avalonia drawing code,
// which a SwiftUI view owns instead.

public enum OutputVisualKind: Sendable, Equatable {
    case generic, dPad, faceButton, keycap, joystick, mouse, shoulder
}

public enum OutputDirection: Sendable, Equatable {
    case n, ne, e, se, s, sw, w, nw
}

public enum ControllerFaceButton: Sendable, Equatable {
    case x, circle, square, triangle, a, b, y
}

public enum ControllerPromptStyle: Sendable, Equatable {
    case playstation, xbox
}

public enum ControllerStickSide: Sendable, Equatable {
    case left, right
}

public extension ControllerType {
    /// Xbox profiles draw the Xbox glyph set. Everything else, including
    /// Nintendo Switch, draws PlayStation glyphs: there is no Switch art set
    /// in this repo, so this is a known gap, not an oversight.
    var promptStyle: ControllerPromptStyle {
        self == .xbox ? .xbox : .playstation
    }
}

/// Presentation metadata for one output token. Token is always the raw
/// firmware value; this type never replaces or normalizes what gets written
/// to a profile.
public struct OutputVisual: Sendable, Equatable {
    public let token: String
    public let kind: OutputVisualKind
    public let friendlyLabel: String
    public var symbol: String?
    public var direction: OutputDirection?
    public var faceButton: ControllerFaceButton?
    public var keycapText: String?
    public var isFallback: Bool = false
    public var promptStyle: ControllerPromptStyle?
    public var stickSide: ControllerStickSide?
    public var assetKey: String?
    public var requiresTextLabel: Bool = false
    public var isStickClick: Bool = false
    public var isTrigger: Bool = false
}

public extension OutputVisual {

    /// Resolves one firmware output token to its presentation. Same branch
    /// order as the C#: dpad, face button, shoulder, stick, mouse, kb_, then
    /// the generic fallback.
    static func `for`(token: String, promptStyle: ControllerPromptStyle? = nil) -> OutputVisual {
        // A CSV line can carry a trailing \r\n, and C#'s Trim() strips that.
        // .whitespaces alone does not, so a token off a device or a sheet
        // fell through to the generic "?" fallback instead of drawing.
        let normalized = token.trimmingCharacters(in: .whitespacesAndNewlines)

        if normalized.hasPrefix("dpad_"), let direction = dPadDirections[String(normalized.dropFirst(5))] {
            let style = promptStyle ?? .playstation
            return OutputVisual(token: token, kind: .dPad, friendlyLabel: dPadLabel(direction),
                                 symbol: directionSymbol(direction), direction: direction,
                                 promptStyle: style, assetKey: dPadAssetKey(style, direction))
        }

        if let (face, faceSymbol) = tryFaceButton(normalized) {
            let style = promptStyle ?? (isXboxToken(normalized) ? .xbox : .playstation)
            return OutputVisual(token: token, kind: .faceButton, friendlyLabel: faceButtonLabel(face),
                                 symbol: faceSymbol, faceButton: face, promptStyle: style,
                                 assetKey: faceButtonAssetKey(style, face, normalized))
        }

        if let (side, isTrigger, xboxVocab) = tryShoulder(normalized) {
            let style = promptStyle ?? (xboxVocab ? .xbox : .playstation)
            var visual = OutputVisual(token: token, kind: .shoulder,
                                       friendlyLabel: shoulderLabel(side, isTrigger),
                                       symbol: shoulderMarking(style, side, isTrigger),
                                       promptStyle: style, stickSide: side)
            visual.isTrigger = isTrigger
            return visual
        }

        if let (side, direction) = tryStick(normalized) {
            let style = promptStyle ?? .playstation
            let isStickClick = direction == nil
            var visual = OutputVisual(token: token, kind: .joystick,
                                       friendlyLabel: stickLabel(side, direction),
                                       symbol: direction.map(directionSymbol),
                                       direction: direction, promptStyle: style, stickSide: side,
                                       assetKey: isStickClick ? stickAssetKey(style, side) : nil)
            visual.isStickClick = isStickClick
            return visual
        }

        if let requiresTextLabel = mouseRequiresTextLabel(normalized) {
            // ponytail: no mouse PNG set. Same reason as the keyboard cut
            // below, so mice resolve to their kind with no asset key.
            return OutputVisual(token: token, kind: .mouse, friendlyLabel: humanize(normalized),
                                 requiresTextLabel: requiresTextLabel)
        }

        if normalized.hasPrefix("kb_") {
            let keycap = keycapLabel(String(normalized.dropFirst(3)))
            // ponytail: dropped the 166-file KeyboardDark/KeyboardLight PNG
            // set. A keycap is ~20 lines of SwiftUI, scales with Dynamic
            // Type, and follows light/dark for free, which a fixed PNG
            // cannot. Upgrade path if a keycap ever looks wrong: the art is
            // in src/QuadStick.App/Assets/OutputVisuals/KeyboardLight|Dark.
            return OutputVisual(token: token, kind: .keycap, friendlyLabel: keycap, keycapText: keycap)
        }

        return OutputVisual(token: token, kind: .generic, friendlyLabel: humanize(normalized),
                             symbol: "?", isFallback: true)
    }

    /// Resolves an action through its firmware keyword, falling back to the
    /// raw catalog id when there is no keyword (an uncurated entry already
    /// stands for its own firmware word).
    static func `for`(_ action: OutputAction, promptStyle: ControllerPromptStyle? = nil) -> OutputVisual {
        let keyword = Firmware.keyword(forOutput: action.id) ?? action.id
        return .for(token: keyword, promptStyle: promptStyle)
    }

    /// The imageset name and rotation (in degrees) behind one asset key, or
    /// nil when no art exists for it. Public so a test can prove every key
    /// the resolver hands out reaches a real imageset.
    static func assetPath(for assetKey: String) -> (name: String, rotation: Double)? {
        if assetKey.hasPrefix("ps:") {
            let name: String?
            switch assetKey.dropFirst(3) {
            case "circle": name = "PS_Circle"
            case "triangle": name = "PS_Triangle"
            case "square": name = "PS_Square"
            case "cross": name = "PS_Cross"
            case "left-stick": name = "PS_LeftStick"
            case "right-stick": name = "PS_RightStick"
            case "dpad": name = "PS_DPad"
            case "dpad-n": name = "PS_DPadNorth"
            case "dpad-e": name = "PS_DPadEast"
            case "dpad-s": name = "PS_DPadSouth"
            case "dpad-w": name = "PS_DPadWest"
            default: name = nil
            }
            return name.map { ($0, 0) }
        }

        if assetKey.hasPrefix("xbox:") {
            // The Xbox files run two ahead of the PlayStation set: file 0007
            // is the left stick, not the d-pad. Reading them in PlayStation
            // order put a thumbstick under "d-pad north" and left south and
            // west with no file at all.
            switch assetKey.dropFirst(5) {
            case "a": return ("Xbox_A", 0)
            case "b": return ("Xbox_B", 0)
            case "y": return ("Xbox_Y", 0)
            case "x": return ("Xbox_X", 0)
            case "left-stick": return ("Xbox_LeftStick", 0)
            case "right-stick": return ("Xbox_RightStick", 0)
            case "dpad": return ("Xbox_DPad", 0)
            case "dpad-n": return ("Xbox_DPadNorth", 0)
            case "dpad-e": return ("Xbox_DPadEast", 0)
            // Xelu's Xbox set stops at north and east. A d-pad cross is
            // symmetric under half a turn, so these two rotated are the real
            // artwork, not a redrawing.
            case "dpad-s": return ("Xbox_DPadNorth", 180)
            case "dpad-w": return ("Xbox_DPadEast", 180)
            default: return nil
            }
        }

        return nil
    }
}

private let dPadDirections: [String: OutputDirection] = [
    "N": .n, "NE": .ne, "E": .e, "SE": .se, "S": .s, "SW": .sw, "W": .w, "NW": .nw,
]

private func dPadLabel(_ direction: OutputDirection) -> String {
    let word: String
    switch direction {
    case .n: word = "north"
    case .ne: word = "northeast"
    case .e: word = "east"
    case .se: word = "southeast"
    case .s: word = "south"
    case .sw: word = "southwest"
    case .w: word = "west"
    case .nw: word = "northwest"
    }
    return "D-pad \(word)"
}

private func directionSymbol(_ direction: OutputDirection) -> String {
    switch direction {
    case .n: return "\u{2191}"
    case .ne: return "\u{2197}"
    case .e: return "\u{2192}"
    case .se: return "\u{2198}"
    case .s: return "\u{2193}"
    case .sw: return "\u{2199}"
    case .w: return "\u{2190}"
    case .nw: return "\u{2196}"
    }
}

// Circle/Square/Triangle keep their PlayStation names. A/B/X/Y fall to the
// default case, so the PS "x" (Cross) token also reads "X button" here,
// matching the desktop's FaceButtonLabel exactly, quirk included.
private func faceButtonLabel(_ button: ControllerFaceButton) -> String {
    switch button {
    case .circle: return "Circle"
    case .square: return "Square"
    case .triangle: return "Triangle"
    case .a: return "A button"
    case .b: return "B button"
    case .y: return "Y button"
    case .x: return "X button"
    }
}

private func shoulderLabel(_ side: ControllerStickSide, _ isTrigger: Bool) -> String {
    switch (side, isTrigger) {
    case (.left, true): return "Left trigger"
    case (.left, false): return "Left bumper"
    case (.right, true): return "Right trigger"
    case (.right, false): return "Right bumper"
    }
}

// What is moulded on the plastic, read off promptStyle, never off a
// translated label: a hardware marking says the same thing in every
// language.
private func shoulderMarking(_ style: ControllerPromptStyle, _ side: ControllerStickSide,
                              _ isTrigger: Bool) -> String {
    let letter = side == .left ? "L" : "R"
    return style == .xbox ? letter + (isTrigger ? "T" : "B") : letter + (isTrigger ? "2" : "1")
}

private func stickLabel(_ side: ControllerStickSide, _ direction: OutputDirection?) -> String {
    let name = side == .left ? "Left Stick" : "Right Stick"
    guard let direction else {
        return side == .left ? "Left stick click" : "Right stick click"
    }
    let word: String
    switch direction {
    case .n: word = "up"
    case .e: word = "right"
    case .s: word = "down"
    default: word = "left"
    }
    return "\(name) \(word)"
}

private func humanize(_ token: String) -> String {
    if token.isEmpty { return "Output" }
    let text = token.replacingOccurrences(of: "_", with: " ")
    guard let first = text.first else { return text }
    return first.uppercased() + text.dropFirst()
}

private func keycapLabel(_ key: String) -> String {
    let lower = key.lowercased()
    switch lower {
    case "space": return "Space"
    case "enter": return "Enter"
    case "return": return "Return"
    case "escape": return "Esc"
    case "backspace": return "Backspace"
    case "tab": return "Tab"
    case "left_arrow": return "\u{2190}"
    case "right_arrow": return "\u{2192}"
    case "up_arrow": return "\u{2191}"
    case "down_arrow": return "\u{2193}"
    default: break
    }
    if lower.count == 1, let c = lower.first, c.isLetter || c.isNumber {
        return lower.uppercased()
    }
    if lower.hasPrefix("f"), Int(lower.dropFirst()) != nil {
        return lower.uppercased()
    }
    return titleWords(lower)
}

private func titleWords(_ value: String) -> String {
    value.split(separator: "_", omittingEmptySubsequences: true)
        .map { word -> String in
            guard let first = word.first else { return String(word) }
            return first.uppercased() + word.dropFirst()
        }
        .joined(separator: " ")
}

private func tryFaceButton(_ token: String) -> (ControllerFaceButton, String)? {
    switch token {
    case "x": return (.x, "\u{00D7}")
    case "circle": return (.circle, "\u{25CB}")
    case "square": return (.square, "\u{25A1}")
    case "triangle": return (.triangle, "\u{25B3}")
    case "A": return (.a, "A")
    case "B": return (.b, "B")
    case "X": return (.x, "X")
    case "Y": return (.y, "Y")
    default: return nil
    }
}

private func tryShoulder(_ token: String) -> (side: ControllerStickSide, isTrigger: Bool, xboxVocab: Bool)? {
    switch token {
    case "left_1": return (.left, false, false)
    case "right_1": return (.right, false, false)
    case "left_2": return (.left, true, false)
    case "right_2": return (.right, true, false)
    case "left_bumper": return (.left, false, true)
    case "right_bumper": return (.right, false, true)
    case "left_trigger": return (.left, true, true)
    case "right_trigger": return (.right, true, true)
    default: return nil
    }
}

private func tryStick(_ token: String) -> (side: ControllerStickSide, direction: OutputDirection?)? {
    let side: ControllerStickSide =
        (token.hasPrefix("right_joy_") || token == "right_stick" || token == "right_3") ? .right : .left
    let direction: OutputDirection?
    switch token {
    case "left_joy_left", "right_joy_left": direction = .w
    case "left_joy_right", "right_joy_right": direction = .e
    case "left_joy_up", "right_joy_up": direction = .n
    case "left_joy_down", "right_joy_down": direction = .s
    default: direction = nil
    }
    // left_3/right_3 is the PS3 word for pressing the stick; left_stick/
    // right_stick is the Xbox word for the same button. Both have to reach
    // the press prompt.
    let isStick = direction != nil
        || token == "left_stick" || token == "right_stick" || token == "left_3" || token == "right_3"
    return isStick ? (side, direction) : nil
}

// Movement tokens carry no direction of their own, so the SwiftUI view
// needs the words next to the silhouette. Click/scroll tokens are
// self-describing and do not.
private func mouseRequiresTextLabel(_ token: String) -> Bool? {
    switch token {
    case "mouse_left_button", "mouse_right_button", "mouse_middle_button",
         "mouse_wheel_up", "mouse_wheel_down", "mouse_back", "mouse_forward":
        return false
    case "mouse_left", "mouse_right", "mouse_up", "mouse_down",
         "mouse_pan_left", "mouse_pan_right":
        return true
    default:
        return nil
    }
}

private func isXboxToken(_ token: String) -> Bool {
    token == "A" || token == "B" || token == "X" || token == "Y"
}

private func dPadAssetKey(_ style: ControllerPromptStyle, _ direction: OutputDirection) -> String {
    let prefix = style == .xbox ? "xbox" : "ps"
    switch direction {
    case .n: return "\(prefix):dpad-n"
    case .e: return "\(prefix):dpad-e"
    case .s: return "\(prefix):dpad-s"
    case .w: return "\(prefix):dpad-w"
    default:
        // No console prompt exists for a diagonal. The neutral pad carries
        // the indicator instead of a key that would match no file.
        return "\(prefix):dpad"
    }
}

// Read off the token, never off a label: a label is translated, and in a
// language whose word for the B button does not start with B, matching on
// its first letter put the same glyph under all four faces. The token is a
// file byte and says the same thing in every language.
private func faceButtonAssetKey(_ style: ControllerPromptStyle, _ face: ControllerFaceButton,
                                 _ token: String) -> String {
    let ps: String
    let xbox: String
    switch token {
    case "x", "A": (ps, xbox) = ("cross", "a")
    case "circle", "B": (ps, xbox) = ("circle", "b")
    case "square", "X": (ps, xbox) = ("square", "x")
    case "triangle", "Y": (ps, xbox) = ("triangle", "y")
    default:
        switch face {
        case .circle: (ps, xbox) = ("circle", "b")
        case .square: (ps, xbox) = ("square", "x")
        case .triangle: (ps, xbox) = ("triangle", "y")
        case .a: (ps, xbox) = ("cross", "a")
        case .b: (ps, xbox) = ("circle", "b")
        case .y: (ps, xbox) = ("triangle", "y")
        case .x: (ps, xbox) = ("square", "x")
        }
    }
    return style == .xbox ? "xbox:\(xbox)" : "ps:\(ps)"
}

private func stickAssetKey(_ style: ControllerPromptStyle, _ side: ControllerStickSide) -> String {
    let prefix = style == .xbox ? "xbox" : "ps"
    return "\(prefix):\(side == .left ? "left-stick" : "right-stick")"
}
