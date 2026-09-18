import Foundation

public enum Severity: String, Codable, Hashable, Sendable {
    case error   // this configuration cannot work
    case warning // unusual but allowed
}

public struct Issue: Identifiable, Hashable, Sendable {
    public let id: String
    public let severity: Severity
    public let message: String    // what is wrong
    public let location: String   // where it is
    public let fix: String        // how to fix it

    public init(id: String, severity: Severity, message: String, location: String, fix: String) {
        self.id = id
        self.severity = severity
        self.message = message
        self.location = location
        self.fix = fix
    }
}

/// Plain-language checks run live on every change, not at save time.
/// The UI's editors already make most invalid values impossible to enter;
/// this backstops them and catches cross-input problems.
public enum ConfigValidator {

    public static func validate(_ profile: Profile, capabilities: DeviceCapabilities) -> [Issue] {
        var issues: [Issue] = []

        if profile.name.trimmingCharacters(in: .whitespaces).isEmpty {
            issues.append(Issue(id: "profile-name", severity: .error,
                                message: "The profile has no name.",
                                location: "Profile settings",
                                fix: "Give the profile a name so you can tell it apart from others."))
        }

        if !DeviceFile.isDeviceReadable(profile.name) {
            issues.append(Issue(id: "profile-name-encoding", severity: .warning,
                                message: "The profile name \"\(profile.name)\" has characters the QuadStick cannot show. The device will list it by its shortened file name instead.",
                                location: "Profile settings",
                                fix: "Use plain letters, numbers, spaces, dashes and underscores if you want to recognise it on the device."))
        }

        var seenNames: [String: Int] = [:]
        for (index, mode) in profile.modes.enumerated() {
            let n = index + 1
            let loc = "Mode \(n) – \(mode.name)"

            if let first = seenNames[mode.name] {
                issues.append(Issue(id: "dup-name-\(mode.id)", severity: .warning,
                                    message: "Mode \(n) and Mode \(first) are both named \"\(mode.name)\". The QuadStick tells modes apart by number, so this works, but it is easy to mix them up.",
                                    location: loc,
                                    fix: "Rename one of them if the shared name is not intentional."))
            } else {
                seenNames[mode.name] = n
            }

            if !DeviceFile.isDeviceReadable(mode.name) {
                issues.append(Issue(id: "mode-name-encoding-\(mode.id)", severity: .warning,
                                    message: "The mode name \"\(mode.name)\" has characters the QuadStick cannot show, so it will look wrong on the device.",
                                    location: loc,
                                    fix: "Use plain letters, numbers, spaces, dashes and underscores."))
            }

            if mode.assignments.values.allSatisfy({ $0.output == nil }) {
                issues.append(Issue(id: "empty-mode-\(mode.id)", severity: .warning,
                                    message: "This mode has no actions assigned. Switching into it makes the QuadStick do nothing.",
                                    location: loc,
                                    fix: "Assign at least one action, or remove the mode."))
            }

            var outputsSeen: [String: String] = [:]
            for (actionID, a) in mode.assignments.sorted(by: { $0.key < $1.key }) {
                // Falls back to the full catalog so a part the chosen model
                // lacks is still named. Reporting a problem against
                // "left-tube-normal-sip" instead of "Left Tube Normal Sip"
                // tells somebody less than nothing.
                let actionName = capabilities.action(actionID)?.fullName
                    ?? QuadStickCatalog.action(actionID)?.fullName
                    ?? actionID

                if let f = a.function {
                    switch f {
                    case .greaterThan(let pct) where !(0...100).contains(pct):
                        issues.append(Issue(id: "gt-\(mode.id)-\(actionID)", severity: .error,
                                            message: "The activation point is \(pct)%, which is not possible. It must be between 0% and 100%.",
                                            location: "\(loc), \(actionName)",
                                            fix: "Set the activation point between 0% and 100%."))
                    case .repeatWhileHeld(let ms) where ms < 10 || ms > 60_000:
                        issues.append(Issue(id: "rep-\(mode.id)-\(actionID)", severity: .error,
                                            message: "The repeat interval is \(InputFunction.seconds(ms)), which is outside what the QuadStick supports.",
                                            location: "\(loc), \(actionName)",
                                            fix: "Use an interval between 0.01 and 60 seconds."))
                    case .delayedLatch(let ms) where ms < 0 || ms > 60_000:
                        issues.append(Issue(id: "delay-\(mode.id)-\(actionID)", severity: .error,
                                            message: "The delay is \(InputFunction.seconds(ms)), which is outside what the QuadStick supports.",
                                            location: "\(loc), \(actionName)",
                                            fix: "Use a delay between 0 and 60 seconds."))
                    default:
                        break
                    }

                    if a.output == nil {
                        issues.append(Issue(id: "fn-noout-\(mode.id)-\(actionID)", severity: .warning,
                                            message: "\(actionName) has a \(f.name) function but no action assigned, so it does nothing.",
                                            location: "\(loc), \(actionName)",
                                            fix: "Assign an action, or remove the function."))
                    }
                }

                if let out = a.output {
                    // Desktop rule (Validator.cs): a name the catalog has never
                    // heard of is a Warning and never an Error. The file may
                    // predate this app's word list, and the device is the judge.
                    if Firmware.keyword(forOutput: out.id) == nil {
                        issues.append(Issue(id: "unknown-out-\(mode.id)-\(actionID)", severity: .warning,
                                            message: "\(out.name) is not a word this app recognises. It is left exactly as it is, and the QuadStick may still accept it.",
                                            location: "\(loc), \(actionName)",
                                            fix: "Check the spelling against your QuadStick's manual, or leave it if it already works."))
                    }

                    if let other = outputsSeen[out.id] {
                        issues.append(Issue(id: "dup-out-\(mode.id)-\(actionID)", severity: .warning,
                                            message: "\(out.name) is assigned to both \(other) and \(actionName). Both will trigger it.",
                                            location: loc,
                                            fix: "This is allowed. Change one of them if it is not intentional."))
                    } else {
                        outputsSeen[out.id] = actionName
                    }
                }
            }
        }

        if profile.modes.isEmpty {
            issues.append(Issue(id: "no-modes", severity: .error,
                                message: "The profile has no modes, so the QuadStick has nothing to load.",
                                location: "Modes",
                                fix: "Add at least one mode."))
        }

        return issues.sorted { a, b in
            if a.severity != b.severity { return a.severity == .error }
            return a.id < b.id
        }
    }
}
