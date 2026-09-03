import Foundation

/// The current QuadStick, described as data so a future generation is a new
/// catalog value, not a rewrite.
public enum QuadStickCatalog {

    private static func tube(_ id: String, _ name: String) -> DeviceInput {
        DeviceInput(id: id, name: name, face: .front, actions: [
            .init(id: "\(id)-normal-sip", name: "Normal Sip", fullName: "\(name) Normal Sip"),
            .init(id: "\(id)-normal-puff", name: "Normal Puff", fullName: "\(name) Normal Puff"),
            .init(id: "\(id)-soft-sip", name: "Soft Sip", fullName: "\(name) Soft Sip"),
            .init(id: "\(id)-soft-puff", name: "Soft Puff", fullName: "\(name) Soft Puff"),
        ])
    }

    private static func jack(_ id: String, _ name: String, inputs: (Int, Int), note: String? = nil) -> DeviceInput {
        DeviceInput(id: id, name: name, face: .back,
                    detail: note ?? "Digital inputs \(inputs.0) and \(inputs.1)",
                    actions: [
                        .init(id: "\(id)-a", name: "Switch \(inputs.0)", fullName: "\(name) Switch \(inputs.0)"),
                        .init(id: "\(id)-b", name: "Switch \(inputs.1)", fullName: "\(name) Switch \(inputs.1)"),
                    ])
    }

    public static let capabilities = DeviceCapabilities(
        model: "QuadStick FPS",
        ledCount: 4,
        inputs: [
            tube("left-tube", "Left Tube"),
            tube("center-tube", "Center Tube"),
            tube("right-tube", "Right Tube"),
            // The fourth sip/puff sensor: the short tube on the right side,
            // usually used to switch modes (firmware right_sip/right_puff).
            DeviceInput(id: "side-tube", name: "Side Tube", face: .front,
                        detail: "The short tube on the right side, often used to switch modes",
                        actions: [
                            .init(id: "side-tube-normal-sip", name: "Normal Sip", fullName: "Side Tube Normal Sip"),
                            .init(id: "side-tube-normal-puff", name: "Normal Puff", fullName: "Side Tube Normal Puff"),
                            .init(id: "side-tube-soft-sip", name: "Soft Sip", fullName: "Side Tube Soft Sip"),
                            .init(id: "side-tube-soft-puff", name: "Soft Puff", fullName: "Side Tube Soft Puff"),
                            .init(id: "side-tube-long-sip", name: "Long Sip", fullName: "Side Tube Long Sip"),
                            .init(id: "side-tube-long-puff", name: "Long Puff", fullName: "Side Tube Long Puff"),
                        ]),
            DeviceInput(id: "joystick", name: "Mouth Joystick", face: .front, actions: [
                .init(id: "joystick-up", name: "Push Up", fullName: "Mouth Joystick Up"),
                .init(id: "joystick-down", name: "Push Down", fullName: "Mouth Joystick Down"),
                .init(id: "joystick-left", name: "Push Left", fullName: "Mouth Joystick Left"),
                .init(id: "joystick-right", name: "Push Right", fullName: "Mouth Joystick Right"),
            ]),
            DeviceInput(id: "lip-switch", name: "Lip Switch", face: .front, actions: [
                .init(id: "lip-press", name: "Press", fullName: "Lip Switch Press"),
                .init(id: "lip-soft-press", name: "Soft Press", fullName: "Lip Switch Soft Press"),
            ]),
            jack("jack-top", "Top Switch Jack", inputs: (7, 8)),
            jack("jack-middle", "Middle Jack", inputs: (5, 6),
                 note: "Lip switch connection. Digital inputs 5 and 6"),
            jack("jack-bottom", "Bottom Switch Jack", inputs: (1, 2)),
            DeviceInput(id: "usb-host", name: "USB Host Port", face: .back,
                        detail: "Plug in an external joystick",
                        actions: [
                            .init(id: "usb-up", name: "Joystick Up", fullName: "USB Joystick Up"),
                            .init(id: "usb-down", name: "Joystick Down", fullName: "USB Joystick Down"),
                            .init(id: "usb-left", name: "Joystick Left", fullName: "USB Joystick Left"),
                            .init(id: "usb-right", name: "Joystick Right", fullName: "USB Joystick Right"),
                        ] + (1...8).map {
                            .init(id: "usb-button-\($0)", name: "Button \($0)", fullName: "USB Joystick Button \($0)")
                        }),
        ]
    )

    /// Every output the firmware accepts. The curated entries below carry the
    /// friendly names people expect ("Left Bumper", not "left_bumper"); every
    /// other firmware word is offered too, named by rule. The id is always the
    /// firmware keyword for uncurated entries, so nothing has to be mapped.
    public static let outputs: [OutputAction] = {
        func many(_ category: OutputCategory, _ names: [String]) -> [OutputAction] {
            names.map { name in
                let id = name.lowercased().replacingOccurrences(of: " ", with: "-")
                return OutputAction(id: "\(category.rawValue.lowercased())-\(id)", name: name, category: category)
            }
        }
        let curated = many(.controller, [
            "A", "B", "X", "Y",
            "Left Trigger", "Right Trigger", "Left Bumper", "Right Bumper",
            "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right",
            "Left Stick Up", "Left Stick Down", "Left Stick Left", "Left Stick Right",
            "Right Stick Up", "Right Stick Down", "Right Stick Left", "Right Stick Right",
            "Left Stick Click", "Right Stick Click", "Start", "Select",
        ])
        + many(.keyboard, ["Space", "Enter", "Escape", "Tab", "Shift", "W", "A", "S", "D", "E", "R", "1", "2", "3"])
        + many(.mouse, ["Left Click", "Right Click", "Middle Click", "Scroll Up", "Scroll Down"])
        + many(.quadstick, ["Volume Up", "Volume Down", "Brightness Up", "Brightness Down", "Restart QuadStick"])
        + many(.modeControl, ["Next Mode", "Previous Mode", "Load Next Profile"])

        // A curated entry already covers this firmware word, so it is not
        // offered twice under its raw name.
        let covered = Set(curated.compactMap { Firmware.outputKeyword[$0.id] })
        let rest = Vocabulary.outputNames
            .filter { !covered.contains($0) && $0 != "none" }
            .map { OutputAction(id: $0, name: displayName(for: $0), category: category(for: $0)) }

        return curated + rest
    }()

    /// Firmware words are grouped by their prefix, which is how the firmware
    /// itself separates them.
    static func category(for keyword: String) -> OutputCategory {
        switch true {
        case keyword.hasPrefix("kb_"):
            return .keyboard
        case keyword.hasPrefix("mouse_"):
            return .mouse
        case keyword.hasPrefix("ir_"):
            return .infrared
        case keyword.hasPrefix("acceleration_"), keyword.hasPrefix("gyroscope_"), keyword.hasPrefix("touch"):
            return .motion
        case keyword.hasPrefix("increment_mode"), keyword.hasPrefix("decrement_mode"), keyword == "load_file":
            return .modeControl
        case keyword.hasPrefix("digital_out"), keyword.hasPrefix("enable_"), keyword.hasPrefix("bluetooth"),
             keyword.hasPrefix("sip_puff"), keyword.hasPrefix("joystick_"), keyword.hasPrefix("deflection_"),
             keyword.hasPrefix("usb_1_"), keyword.hasPrefix("usb_2_"),
             keyword == "volume", keyword == "brightness", keyword == "anti_dead_zone",
             keyword == "watchdog_disable", keyword == "debug", keyword == "reset_quadstick",
             keyword == "ps4_authentication", keyword == "mouse_speed", keyword == "mouse_response_curve",
             keyword.hasPrefix("lip_position"):
            return .quadstick
        default:
            return .controller
        }
    }

    /// "left_joy_up" reads as "Left Joy Up". Prefixes that mean something to a
    /// person are spelled out; the rest is the firmware word, tidied. The raw
    /// word stays the id, so nothing the user picked is ever rewritten.
    static func displayName(for keyword: String) -> String {
        var body = keyword
        var lead = ""
        // Longest first: ir_tv_ has to win over ir_, or names read "TV Tv ...".
        for (prefix, label) in [("kb_", "Keyboard"), ("ir_tv_", "TV"), ("ir_", "Remote"),
                                ("xac_", "Adaptive Controller"),
                                ("usb_1_", "USB Joystick 1"), ("usb_2_", "USB Joystick 2")] {
            if body.hasPrefix(prefix) {
                lead = label
                body = String(body.dropFirst(prefix.count))
                break
            }
        }
        let words = body.split(separator: "_").map { part -> String in
            let s = String(part)
            // N, NE, SW and single letters are already how the firmware and the
            // keycaps read them.
            if s.count <= 2, s == s.uppercased() { return s }
            if ["ps3", "ps4", "usb", "ds3", "ir", "tv", "gui", "cw", "ccw"].contains(s.lowercased()) {
                return s.uppercased()
            }
            // Words VoiceOver would otherwise say wrong.
            if s.lowercased() == "dpad" { return "D-pad" }
            if s.lowercased().hasPrefix("out"), let n = s.last, n.isNumber {
                return "Out \(n)"
            }
            return s.prefix(1).uppercased() + s.dropFirst()
        }
        return ([lead] + words).filter { !$0.isEmpty }.joined(separator: " ")
    }

    public static func output(_ id: String) -> OutputAction? {
        outputs.first { $0.id == id }
    }
}
