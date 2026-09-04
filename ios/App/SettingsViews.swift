import SwiftUI
import QuadStickKit

/// The QuadStick's own device-wide settings. This app does not read them and
/// does not write them: they live in the device's Preferences file, which a
/// profile install never touches. Showing numbers here would mean showing this
/// app's defaults dressed up as the device's real values, so the screen names
/// each setting and says where it actually lives.
struct QuadStickSettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Form {
            // This is not a device setting, it is a display choice this app
            // makes on its own: which photo and which parts to show. Its own
            // section, separate from the read-only list below, so the two
            // kinds of setting are never mistaken for each other.
            Section {
                Picker("Which QuadStick do you have?", selection: modelBinding) {
                    ForEach(QuadStickModel.allCases, id: \.self) { m in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(m.displayName)
                            Text(m.summary)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        .tag(m)
                    }
                }
                .pickerStyle(.inline)
                // The inline picker already prints its own label as the
                // first row, so a Section header here would say the same
                // words twice.
            } footer: {
                Text("This changes what SipStudio shows and does. It does not change any setting on the QuadStick itself.")
            }

            Section {
                settingRow("Joystick sensitivity",
                           "How far the joystick moves for a given mouth movement.")
                settingRow("Sip and puff activation strength",
                           "How hard a sip or puff has to be before the QuadStick reacts.")
                settingRow("Dead zone",
                           "How much joystick movement near the centre is ignored. Helps with joystick drift.")
                settingRow("Consoles and connections",
                           "Boot in PS4 mode, the Titan Two PS4 flag, and USB host mode for an external joystick.")
            } header: {
                Text("Settings on your QuadStick")
            } footer: {
                Text("These belong to the QuadStick, not a profile, so they stay the same across profiles. Installing from here changes only the profile.")
            }

            Section {
                Label("Change them in the desktop QuadStick Config Manager or the QuadStick's own menus.",
                      systemImage: "desktopcomputer")
                    .font(.footnote)
            }
        }
        .navigationTitle("QuadStick Settings")
    }

    private var modelBinding: Binding<QuadStickModel> {
        Binding(
            get: { model.settings.deviceModel },
            set: { m in
                model.settings.deviceModel = m
                model.saveSettings()
            }
        )
    }

    private func settingRow(_ title: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(.body.weight(.medium))
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
        .padding(.vertical, 4)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(title). \(detail)")
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
                    Text("Which controller the QuadStick emulates while this profile is loaded.")
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
