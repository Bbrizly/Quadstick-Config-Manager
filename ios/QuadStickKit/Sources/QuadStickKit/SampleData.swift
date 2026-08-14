import Foundation

/// Seed content for the mock repository: one believable game profile.
public enum SampleData {

    private static func out(_ name: String, _ category: OutputCategory = .controller) -> OutputAction {
        let id = name.lowercased().replacingOccurrences(of: " ", with: "-")
        return QuadStickCatalog.output("\(category.rawValue.lowercased())-\(id)")
            ?? OutputAction(id: id, name: name, category: category)
    }

    public static var fortnite: Profile {
        Profile(
            name: "Fortnite",
            controllerType: .playstation,
            modes: [
                Mode(name: "Movement", assignments: [
                    "left-tube-normal-sip": .init(output: out("A"), label: "Jump"),
                    "left-tube-normal-puff": .init(output: out("X"), label: "Reload"),
                    "left-tube-soft-puff": .init(output: out("D-pad Up"), label: "Ping"),
                    "center-tube-normal-sip": .init(output: out("Left Stick Click"), label: "Sprint"),
                    "center-tube-normal-puff": .init(output: out("B"), label: "Crouch"),
                    "right-tube-normal-sip": .init(output: out("Right Trigger"), label: "Fire"),
                    "right-tube-normal-puff": .init(output: out("Left Trigger"), label: "Aim"),
                    "joystick-up": .init(output: out("Left Stick Up")),
                    "joystick-down": .init(output: out("Left Stick Down")),
                    "joystick-left": .init(output: out("Left Stick Left")),
                    "joystick-right": .init(output: out("Left Stick Right")),
                    "lip-press": .init(output: out("Next Mode", .modeControl)),
                ]),
                Mode(name: "Building", assignments: [
                    "left-tube-normal-sip": .init(output: out("Right Bumper"), label: "Wall"),
                    "left-tube-normal-puff": .init(output: out("Right Trigger"), label: "Place"),
                    "right-tube-normal-sip": .init(output: out("Left Bumper"), label: "Floor"),
                    "right-tube-normal-puff": .init(output: out("Y"), label: "Edit",
                                                    function: .delayedLatch(delayMS: 500)),
                    "lip-press": .init(output: out("Next Mode", .modeControl)),
                ]),
                Mode(name: "Driving", assignments: [
                    "left-tube-normal-sip": .init(output: out("Right Trigger"), label: "Accelerate"),
                    "left-tube-normal-puff": .init(output: out("Left Trigger"), label: "Brake"),
                    "right-tube-normal-puff": .init(output: out("A"), label: "Exit vehicle"),
                    "lip-press": .init(output: out("Next Mode", .modeControl)),
                ]),
                Mode(name: "Menu", assignments: [
                    "left-tube-normal-sip": .init(output: out("A"), label: "Confirm"),
                    "left-tube-normal-puff": .init(output: out("B"), label: "Back"),
                    "joystick-up": .init(output: out("D-pad Up")),
                    "joystick-down": .init(output: out("D-pad Down")),
                    "joystick-left": .init(output: out("D-pad Left")),
                    "joystick-right": .init(output: out("D-pad Right")),
                    "lip-press": .init(output: out("Next Mode", .modeControl)),
                ]),
            ]
        )
    }

    public static var minecraft: Profile {
        Profile(
            name: "Minecraft",
            modes: [
                Mode(name: "Explore", assignments: [
                    "left-tube-normal-sip": .init(output: out("A"), label: "Jump"),
                    "right-tube-normal-sip": .init(output: out("Right Trigger"), label: "Mine",
                                                   function: .repeatWhileHeld(intervalMS: 200)),
                    "right-tube-normal-puff": .init(output: out("Left Trigger"), label: "Place block"),
                    "lip-press": .init(output: out("Next Mode", .modeControl)),
                ]),
                Mode(name: "Inventory", assignments: [
                    "left-tube-normal-sip": .init(output: out("A"), label: "Select"),
                    "left-tube-normal-puff": .init(output: out("B"), label: "Close"),
                ]),
            ]
        )
    }
}
