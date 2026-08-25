import SwiftUI
import QuadStickKit

@main
struct SipStudioApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup {
            HomeView()
                .environment(model)
                .preferredColorScheme(.dark)
                .tint(Theme.accent)
        }
    }
}

/// One observable model for the whole app. Every mutation goes through
/// mutate(), which snapshots for undo and autosaves.
@Observable
final class AppModel {
    var profiles: [Profile] = []
    var profileIndex: Int = 0
    var settings = GlobalSettings()
    var modeIndex: Int = 0
    var face: DeviceFace = .front

    let capabilities = QuadStickCatalog.capabilities
    private let repo: ConfigurationRepository
    private var undoStack: [Profile] = []

    init(repo: ConfigurationRepository = MockConfigurationRepository()) {
        self.repo = repo
        do {
            profiles = try repo.loadProfiles()
            settings = try repo.loadSettings()
        } catch {
            // Corrupt local data falls back to samples but says so, never silently.
            loadError = "Saved profiles could not be read (\(error.localizedDescription)). Showing sample data instead."
            profiles = [SampleData.fortnite]
        }
    }

    var loadError: String?

    var profile: Profile {
        get { profiles.indices.contains(profileIndex) ? profiles[profileIndex] : SampleData.fortnite }
        set {
            guard profiles.indices.contains(profileIndex) else { return }
            profiles[profileIndex] = newValue
        }
    }

    var mode: Mode {
        let modes = profile.modes
        return modes.indices.contains(modeIndex) ? modes[modeIndex] : Mode(name: "Empty")
    }

    var issues: [Issue] {
        ConfigValidator.validate(profile, capabilities: capabilities)
    }

    var canUndo: Bool { !undoStack.isEmpty }

    func mutate(_ change: (inout Profile) -> Void) {
        undoStack.append(profile)
        var p = profile
        change(&p)
        profile = p
        autosave()
    }

    /// Any change to the list of modes, with the same mode still active after.
    /// The firmware tells modes apart by position, so reordering renumbers
    /// them. Following the slot instead of the mode swaps somebody's controls
    /// without saying so, which is why every list edit goes through here.
    func mutateModes(_ change: (inout [Mode]) -> Void) {
        let activeID = mode.id
        mutate { change(&$0.modes) }
        modeIndex = indexOfMode(activeID) ?? min(modeIndex, max(0, profile.modes.count - 1))
    }

    func indexOfMode(_ id: Mode.ID) -> Int? {
        profile.modes.firstIndex { $0.id == id }
    }

    func undo() {
        guard let last = undoStack.popLast() else { return }
        let activeID = mode.id
        profile = last
        modeIndex = indexOfMode(activeID) ?? min(modeIndex, max(0, profile.modes.count - 1))
        autosave()
    }

    func saveSettings() {
        try? repo.saveSettings(settings)
    }

    func selectProfile(_ index: Int) {
        guard profiles.indices.contains(index) else { return }
        profileIndex = index
        modeIndex = 0
        undoStack.removeAll()
    }

    /// Adds and selects. Used by New, Duplicate and both import paths.
    func addProfile(_ p: Profile) {
        profiles.append(p)
        autosave()
        selectProfile(profiles.count - 1)
    }

    func deleteProfile(at index: Int) {
        guard profiles.count > 1, profiles.indices.contains(index) else { return }
        profiles.remove(at: index)
        if profileIndex >= profiles.count { profileIndex = profiles.count - 1 }
        modeIndex = 0
        undoStack.removeAll()
        autosave()
    }

    func assignment(for actionID: String) -> Assignment {
        mode.assignments[actionID] ?? Assignment()
    }

    func setAssignment(_ assignment: Assignment, for actionID: String) {
        let index = modeIndex
        mutate { p in
            guard p.modes.indices.contains(index) else { return }
            if assignment.output == nil && assignment.function == nil {
                p.modes[index].assignments.removeValue(forKey: actionID)
            } else {
                p.modes[index].assignments[actionID] = assignment
            }
        }
    }

    private func autosave() {
        try? repo.saveProfiles(profiles)
    }

    /// Assignments on an input, in catalog order, for lists and VoiceOver.
    func summary(of input: DeviceInput) -> [(action: InputActionDef, assignment: Assignment)] {
        input.actions.map { ($0, assignment(for: $0.id)) }
    }

    func voiceOverSummary(of input: DeviceInput) -> String {
        let assigned = summary(of: input).filter { $0.assignment.output != nil }
        guard !assigned.isEmpty else { return "\(input.name). Nothing assigned." }
        let parts = assigned.map { "\($0.action.name) assigned to \($0.assignment.display)" }
        return "\(input.name). \(parts.joined(separator: ". "))."
    }
}
