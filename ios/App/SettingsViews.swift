import SwiftUI
import QuadStickKit

/// Device-wide settings in human language. Raw numbers live under Advanced.
struct QuadStickSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var showAdvanced = false

    var body: some View {
        @Bindable var model = model
        Form {
            Section {
                labeledSlider(
                    title: "Joystick sensitivity",
                    value: intBinding(\.joystickSensitivity), range: 1...100,
                    low: "Less sensitive", high: "More sensitive")
            } footer: {
                Text("More sensitive: smaller mouth movements produce larger joystick movement. Less sensitive: requires more movement for the same output.")
            }

            Section {
                labeledSlider(
                    title: "Sip and puff activation strength",
                    value: intBinding(\.sipPuffThreshold), range: 1...100,
                    low: "Easier to activate", high: "Harder to activate")
            } footer: {
                Text("How hard a sip or puff has to be before the QuadStick reacts.")
            }

            Section {
                labeledSlider(
                    title: "Dead zone",
                    value: intBinding(\.deadZone), range: 0...30,
                    low: "Off", high: "Larger")
            } footer: {
                Text("Ignores very small joystick movement near the center. Useful when an older joystick drifts.")
            }

            Section("Consoles and connections") {
                Toggle("Boot in PS4 mode", isOn: settingBinding(\.bootPS4))
                Toggle("Titan Two PS4 flag", isOn: settingBinding(\.titanTwoPS4))
                Toggle("USB host mode", isOn: settingBinding(\.usbHostMode))
            }
            Section {
                EmptyView()
            } footer: {
                Text("Boot in PS4 mode: needed for some console adapters. Titan Two PS4 flag: tells a connected Titan Two adapter to present as a PS4 controller. USB host mode: enable when using an external joystick through the QuadStick's rear USB port.")
            }

            Section {
                DisclosureGroup("Advanced", isExpanded: $showAdvanced) {
                    row("Joystick sensitivity", "\(model.settings.joystickSensitivity)")
                    row("Sip/puff threshold", "\(model.settings.sipPuffThreshold)")
                    row("Dead zone", "\(model.settings.deadZone)%")
                    Text("These are app units for now. Exact firmware preference values appear here once a QuadStick is connected.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .navigationTitle("QuadStick Settings")
    }

    private func row(_ title: String, _ value: String) -> some View {
        HStack {
            Text(title)
            Spacer()
            Text(value).foregroundStyle(.secondary).monospacedDigit()
        }
    }

    private func labeledSlider(title: String, value: Binding<Double>,
                               range: ClosedRange<Double>, low: String, high: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
            Slider(value: value, in: range, step: 1) {
                Text(title)
            } minimumValueLabel: {
                Text(low).font(.caption2)
            } maximumValueLabel: {
                Text(high).font(.caption2)
            }
            .accessibilityValue("\(Int(value.wrappedValue)) of \(Int(range.upperBound))")
        }
    }

    private func intBinding(_ path: WritableKeyPath<GlobalSettings, Int>) -> Binding<Double> {
        Binding(
            get: { Double(model.settings[keyPath: path]) },
            set: { model.settings[keyPath: path] = Int($0); model.saveSettings() }
        )
    }

    private func settingBinding(_ path: WritableKeyPath<GlobalSettings, Bool>) -> Binding<Bool> {
        Binding(
            get: { model.settings[keyPath: path] },
            set: { model.settings[keyPath: path] = $0; model.saveSettings() }
        )
    }
}

struct ProfileSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var showAdvanced = false

    var body: some View {
        Form {
            Section {
                TextField("Profile name", text: nameBinding)
            } header: {
                Text("Name")
            } footer: {
                Text("A profile is the complete setup for one game or activity.")
            }

            Section {
                Picker("Controller type", selection: controllerBinding) {
                    ForEach(ControllerType.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
            } header: {
                Text("Controller")
            } footer: {
                Text("What kind of controller the QuadStick pretends to be while this profile is loaded.")
            }

            Section {
                DisclosureGroup("Advanced details", isExpanded: $showAdvanced) {
                    HStack {
                        Text("Firmware mode")
                        Spacer()
                        Text("\(model.profile.controllerType.firmwareMode)")
                            .foregroundStyle(.secondary).monospacedDigit()
                    }
                }
            }
        }
        .navigationTitle("Profile Settings")
    }

    private var nameBinding: Binding<String> {
        Binding(
            get: { model.profile.name },
            set: { name in model.mutate { $0.name = name } }
        )
    }

    private var controllerBinding: Binding<ControllerType> {
        Binding(
            get: { model.profile.controllerType },
            set: { t in model.mutate { $0.controllerType = t } }
        )
    }
}
