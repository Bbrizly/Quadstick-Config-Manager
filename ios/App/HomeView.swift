import SwiftUI
import QuadStickKit

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var showIssues = false
    @State private var path = NavigationPath()

    var body: some View {
        @Bindable var model = model
        NavigationStack(path: $path) {
            ScrollView {
                VStack(spacing: 20) {
                    header

                    Picker("Side of the QuadStick", selection: $model.face) {
                        Text("Front").tag(DeviceFace.front)
                        Text("Back").tag(DeviceFace.back)
                    }
                    .pickerStyle(.segmented)

                    if model.face == .front {
                        DevicePhotoView()
                    } else {
                        DeviceBackView()
                    }

                    Text("Tap a part of the QuadStick to see and change what it does.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)

                    validationStatus

                    if let loadError = model.loadError {
                        Label(loadError, systemImage: "exclamationmark.triangle")
                            .font(.footnote)
                            .foregroundStyle(.orange)
                    }

                    navigationButtons
                }
                .padding()
                .frame(maxWidth: 640)
                .frame(maxWidth: .infinity)
            }
            .background(Theme.background)
            .navigationTitle("SipStudio")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("Undo", systemImage: "arrow.uturn.backward") { model.undo() }
                        .disabled(!model.canUndo)
                }
            }
            .navigationDestination(for: DeviceInput.self) { InputDetailView(input: $0) }
            .navigationDestination(for: String.self) { actionID in
                if let action = model.capabilities.action(actionID) {
                    ActionEditorView(action: action)
                }
            }
            .navigationDestination(for: Screen.self) { screen in
                switch screen {
                case .modes: ModesView()
                case .deviceSettings: QuadStickSettingsView()
                case .profileSettings: ProfileSettingsView()
                case .review: ReviewView()
                case .profiles: ProfilesView()
                case .install: InstallView()
                case .drive: DriveView()
                }
            }
            .sheet(isPresented: $showIssues) { ValidationListView() }
        }
    }

    private var header: some View {
        VStack(spacing: 8) {
            Menu {
                ForEach(Array(model.profiles.enumerated()), id: \.element.id) { index, p in
                    Button {
                        model.selectProfile(index)
                    } label: {
                        if index == model.profileIndex {
                            Label(p.name, systemImage: "checkmark")
                        } else {
                            Text(p.name)
                        }
                    }
                }
                Divider()
                Button("All profiles") {
                    path.append(Screen.profiles)
                }
            } label: {
                HStack(spacing: 6) {
                    Text(model.profile.name)
                        .font(.largeTitle.bold())
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Profile: \(model.profile.name). Double tap to switch profiles.")

            // The name goes on its own line so the arrows stay beside the
            // short half. Together on one line they strand the arrows against
            // three wrapped lines at accessibility text sizes.
            VStack(spacing: 4) {
                Text(model.mode.name)
                    .font(.headline)
                    .multilineTextAlignment(.center)
                HStack(spacing: 16) {
                    Button("Previous mode", systemImage: "chevron.left") {
                        model.modeIndex = (model.modeIndex - 1 + model.profile.modes.count) % model.profile.modes.count
                    }
                    .labelStyle(.iconOnly)
                    .frame(minWidth: 44, minHeight: 44)
                    Text("Mode \(model.modeIndex + 1) of \(model.profile.modes.count)")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Button("Next mode", systemImage: "chevron.right") {
                        model.modeIndex = (model.modeIndex + 1) % model.profile.modes.count
                    }
                    .labelStyle(.iconOnly)
                    .frame(minWidth: 44, minHeight: 44)
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Current mode: \(model.mode.name), mode \(model.modeIndex + 1) of \(model.profile.modes.count)")
        }
    }

    @ViewBuilder
    private var validationStatus: some View {
        let issues = model.issues
        let errors = issues.filter { $0.severity == .error }.count
        let warnings = issues.count - errors
        if issues.isEmpty {
            // A clean profile is a statement, not an unavailable button. As a
            // disabled Button it drew dimmed and read out as "dimmed", which is
            // the opposite of what a passing check should say.
            Label("No problems found", systemImage: "checkmark.circle")
                .font(.subheadline)
                .foregroundStyle(.green)
                .accessibilityLabel("No problems found in this profile.")
        } else {
            Button {
                showIssues = true
            } label: {
                Label(statusText(errors: errors, warnings: warnings),
                      systemImage: errors > 0 ? "exclamationmark.octagon" : "exclamationmark.triangle")
            }
            .buttonStyle(.bordered)
            .tint(errors > 0 ? .red : .orange)
            .accessibilityHint("Opens the list of problems")
        }
    }

    private func statusText(errors: Int, warnings: Int) -> String {
        var parts: [String] = []
        if errors > 0 { parts.append("\(errors) error\(errors == 1 ? "" : "s")") }
        if warnings > 0 { parts.append("\(warnings) warning\(warnings == 1 ? "" : "s")") }
        return parts.joined(separator: ", ")
    }

    private var navigationButtons: some View {
        VStack(spacing: 10) {
            NavigationLink(value: Screen.profiles) {
                homeRow("Profiles", icon: "person.crop.rectangle.stack",
                        detail: "Switch games or import a shared profile")
            }
            NavigationLink(value: Screen.modes) {
                homeRow("Modes", icon: "square.stack.3d.up",
                        detail: "Control layouts inside this profile")
            }
            NavigationLink(value: Screen.profileSettings) {
                homeRow("Profile Settings", icon: "gamecontroller",
                        detail: "Name and controller type for \(model.profile.name)")
            }
            NavigationLink(value: Screen.deviceSettings) {
                homeRow("QuadStick Settings", icon: "slider.horizontal.3",
                        detail: "Settings that live on the device, not in this profile")
            }
            NavigationLink(value: Screen.review) {
                homeRow("Review Controls", icon: "checklist",
                        detail: "Walk through every input, one at a time")
            }
            NavigationLink(value: Screen.drive) {
                homeRow("Google Drive", icon: "arrow.triangle.2.circlepath",
                        detail: driveDetail, iconColor: Theme.accent)
            }
            NavigationLink(value: Screen.install) {
                homeRow("Install to QuadStick", icon: "arrow.down.circle",
                        detail: "Put \(model.profile.name) on the device with a USB-C cable")
            }
        }
    }

    // What Drive is doing for this profile, in the row itself, so nobody has
    // to open the screen to find out whether it is backed up.
    private var driveDetail: String {
        switch model.drive.status {
        case .notConfigured: return "Not set up in this build"
        case .signedOut: return "Back up and share this profile with a link"
        case .working(let message): return message
        case .problem(let message): return message
        case .signedIn:
            return model.profile.sheetID == nil
                ? "Not backed up yet"
                : "Backed up. Tap to share or update it."
        }
    }

    private func homeRow(_ title: String, icon: String, detail: String, iconColor: Color? = nil) -> some View {
        HStack(spacing: 14) {
            Image(systemName: icon)
                .font(.title3)
                .frame(width: 32)
                .foregroundStyle(iconColor ?? Theme.accent)
            VStack(alignment: .leading, spacing: 2) {
                // Concrete colours: NavigationLink tints its label, and the
                // hierarchical styles resolve against that tint.
                Text(title).font(.headline).foregroundStyle(.white)
                Text(detail).font(.caption).foregroundStyle(Color(white: 0.65))
            }
            Spacer()
            Image(systemName: "chevron.right")
                .font(.caption)
                .foregroundStyle(.tertiary)
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 14)
        .background(RoundedRectangle(cornerRadius: 12).fill(Theme.card))
        .contentShape(Rectangle())
    }
}

enum Screen: Hashable {
    case modes, deviceSettings, profileSettings, review, profiles, install, drive
}
