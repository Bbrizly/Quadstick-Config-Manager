import Foundation

// Mock data layer for the configurator UX. The desktop C# backend
// (qsf_apply, see docs/specs/20260814-shipaton-ios-configurator.md)
// replaces the storage side later; these types are the UI's view of a
// profile, not the CSV.

public enum DeviceFace: String, Codable, Sendable {
    case front, back
}

/// One assignable action on a physical input, e.g. "Left Tube Normal Sip".
public struct InputActionDef: Identifiable, Codable, Hashable, Sendable {
    public let id: String
    public let name: String       // "Normal Sip"
    public let fullName: String   // "Left Tube Normal Sip"

    public init(id: String, name: String, fullName: String) {
        self.id = id
        self.name = name
        self.fullName = fullName
    }
}

/// A physical, tappable part of the QuadStick.
public struct DeviceInput: Identifiable, Codable, Hashable, Sendable {
    public let id: String
    public let name: String       // "Left Tube"
    public let face: DeviceFace
    public let detail: String?    // "Digital inputs 7 and 8" for jacks
    public let actions: [InputActionDef]

    public init(id: String, name: String, face: DeviceFace, detail: String? = nil, actions: [InputActionDef]) {
        self.id = id
        self.name = name
        self.face = face
        self.detail = detail
        self.actions = actions
    }
}

/// What one QuadStick generation physically has. Future hardware ships a
/// different capabilities value, not different views.
public struct DeviceCapabilities: Codable, Sendable {
    public let model: String
    public let ledCount: Int
    public let inputs: [DeviceInput]

    public init(model: String, ledCount: Int, inputs: [DeviceInput]) {
        self.model = model
        self.ledCount = ledCount
        self.inputs = inputs
    }

    public func input(_ id: String) -> DeviceInput? {
        inputs.first { $0.id == id }
    }

    public func input(forAction actionID: String) -> DeviceInput? {
        inputs.first { $0.actions.contains { $0.id == actionID } }
    }

    public func action(_ id: String) -> InputActionDef? {
        for input in inputs {
            if let a = input.actions.first(where: { $0.id == id }) { return a }
        }
        return nil
    }
}

public enum OutputCategory: String, Codable, CaseIterable, Hashable, Sendable {
    case controller = "Controller"
    case keyboard = "Keyboard"
    case mouse = "Mouse"
    case quadstick = "QuadStick"
    case modeControl = "Mode & Profile"
}

/// Something an input can do: press B, type space, switch mode.
public struct OutputAction: Identifiable, Codable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let category: OutputCategory

    public init(id: String, name: String, category: OutputCategory) {
        self.id = id
        self.name = name
        self.category = category
    }
}

/// Timing/behavior modifier on an assignment. Millisecond values are stored
/// exactly as entered, never rounded (house rule: never rewrite a value the
/// user did not type).
public enum InputFunction: Codable, Hashable, Sendable {
    case toggle
    case repeatWhileHeld(intervalMS: Int)
    case greaterThan(percent: Int)
    case delayedLatch(delayMS: Int)

    public var name: String {
        switch self {
        case .toggle: "Toggle"
        case .repeatWhileHeld: "Repeat"
        case .greaterThan: "Greater Than"
        case .delayedLatch: "Delayed Latch"
        }
    }

    public var explanation: String {
        switch self {
        case .toggle:
            "Press once to turn on. Press again to turn off."
        case .repeatWhileHeld:
            "While the input is held, the action repeats at this interval."
        case .greaterThan:
            "The action activates only when the input is stronger than this point."
        case .delayedLatch:
            "The action activates after the input has been held for this long."
        }
    }

    public var summary: String {
        switch self {
        case .toggle:
            "Toggle"
        case .repeatWhileHeld(let ms):
            "Repeats every \(Self.seconds(ms))"
        case .greaterThan(let pct):
            "Activates above \(pct)%"
        case .delayedLatch(let ms):
            "Waits \(Self.seconds(ms))"
        }
    }

    public static func seconds(_ ms: Int) -> String {
        let s = Double(ms) / 1000.0
        let text = s == s.rounded() ? String(format: "%.0f", s) : String(format: "%.2g", s)
        return "\(text) second\(s == 1 ? "" : "s")"
    }
}

public struct Assignment: Codable, Hashable, Sendable {
    public var output: OutputAction?
    /// Clinician's name for what this does in the game, e.g. "Jump".
    public var label: String?
    public var function: InputFunction?

    public init(output: OutputAction? = nil, label: String? = nil, function: InputFunction? = nil) {
        self.output = output
        self.label = label
        self.function = function
    }

    /// "Jump (A)" if labeled, "A" if not, "Unassigned" if empty.
    public var display: String {
        guard let output else { return "Unassigned" }
        if let label, !label.isEmpty { return "\(label) (\(output.name))" }
        return output.name
    }
}

public struct Mode: Identifiable, Codable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var assignments: [String: Assignment]   // key: InputActionDef.id

    public init(id: UUID = UUID(), name: String, assignments: [String: Assignment] = [:]) {
        self.id = id
        self.name = name
        self.assignments = assignments
    }
}

public enum ControllerType: String, Codable, CaseIterable, Hashable, Sendable {
    case standard = "Default"
    case playstation = "PlayStation"
    case nintendoSwitch = "Nintendo Switch"
    case xbox = "Xbox (adapter)"

    // ponytail: placeholder numbers except Switch (5, verified in FW 2373);
    // real values come from qsf_catalogs when the C# backend is wired in.
    public var firmwareMode: Int {
        switch self {
        case .standard: 0
        case .playstation: 1
        case .nintendoSwitch: 5
        case .xbox: 2
        }
    }
}

public struct Profile: Identifiable, Codable, Hashable, Sendable {
    public var id: UUID
    public var name: String
    public var controllerType: ControllerType
    public var modes: [Mode]

    public init(id: UUID = UUID(), name: String, controllerType: ControllerType = .standard, modes: [Mode]) {
        self.id = id
        self.name = name
        self.controllerType = controllerType
        self.modes = modes
    }
}

/// Device-wide settings, in UI units. Raw firmware preference names stay out
/// of the model; the repository translates when the real backend lands.
public struct GlobalSettings: Codable, Hashable, Sendable {
    public var joystickSensitivity: Int   // 1...100, higher = more sensitive
    public var sipPuffThreshold: Int      // 1...100, higher = harder to activate
    public var deadZone: Int              // 0...30, percent of travel ignored
    public var bootPS4: Bool
    public var titanTwoPS4: Bool
    public var usbHostMode: Bool

    public init(joystickSensitivity: Int = 50, sipPuffThreshold: Int = 40, deadZone: Int = 5,
                bootPS4: Bool = false, titanTwoPS4: Bool = false, usbHostMode: Bool = false) {
        self.joystickSensitivity = joystickSensitivity
        self.sipPuffThreshold = sipPuffThreshold
        self.deadZone = deadZone
        self.bootPS4 = bootPS4
        self.titanTwoPS4 = titanTwoPS4
        self.usbHostMode = usbHostMode
    }
}
