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
                        ]),
            DeviceInput(id: "joystick", name: "Mouth Joystick", face: .front, actions: [
                .init(id: "joystick-up", name: "Push Up", fullName: "Mouth Joystick Up"),
                .init(id: "joystick-down", name: "Push Down", fullName: "Mouth Joystick Down"),
                .init(id: "joystick-left", name: "Push Left", fullName: "Mouth Joystick Left"),
                .init(id: "joystick-right", name: "Push Right", fullName: "Mouth Joystick Right"),
            ]),
            DeviceInput(id: "lip-switch", name: "Lip Switch", face: .front, actions: [
                .init(id: "lip-press", name: "Press", fullName: "Lip Switch Press"),
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

    // ponytail: curated mock output list; the real vocabulary comes from
    // qsf_catalogs (firmware Vocab) when the C# backend is wired in.
    public static let outputs: [OutputAction] = {
        func many(_ category: OutputCategory, _ names: [String]) -> [OutputAction] {
            names.map { name in
                let id = name.lowercased().replacingOccurrences(of: " ", with: "-")
                return OutputAction(id: "\(category.rawValue.lowercased())-\(id)", name: name, category: category)
            }
        }
        return many(.controller, [
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
    }()

    public static func output(_ id: String) -> OutputAction? {
        outputs.first { $0.id == id }
    }
}
