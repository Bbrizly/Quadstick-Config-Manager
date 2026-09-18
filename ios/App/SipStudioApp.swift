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
@MainActor
final class AppModel {
    var profiles: [Profile] = []
    var profileIndex: Int = 0
    var settings = GlobalSettings()
    var modeIndex: Int = 0
    var face: DeviceFace = .front

    // Derives from settings.deviceModel so switching the picker in
    // QuadStickSettingsView changes what the whole app shows. Filters a
    // short fixed array, so cheap to recompute on every read.
    var capabilities: DeviceCapabilities { QuadStickCatalog.capabilities(for: settings.deviceModel) }
    let drive = DriveAccount()
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

    // MARK: - Google Drive

    /// Record where a profile now lives on Drive. Bookkeeping, not an edit: it
    /// never lands in undo, because undoing a backup is not a thing anybody
    /// means to do.
    func rememberSheet(_ id: String, syncedAt time: String?) {
        guard profiles.indices.contains(profileIndex) else { return }
        profiles[profileIndex].sheetID = id
        profiles[profileIndex].sheetSyncedTime = time
        autosave()
    }

    /// The sheet won. Take its contents into the open profile, keeping the
    /// profile's own identity so it stays the same row in the list and stays
    /// pointed at the same sheet.
    ///
    /// Undoable, because this one really does replace somebody's work, and the
    /// notes are surfaced rather than swallowed: an import that drops a row has
    /// to say so.
    @discardableResult
    func adoptOnlineVersion(_ csv: String, sheetID: String) -> [String] {
        guard let imported = DeviceFile.importProfile(csv: csv, fallbackName: profile.name) else {
            return ["The sheet did not contain a profile this app could read. Nothing was changed."]
        }
        mutate { p in
            p.name = imported.profile.name
            p.controllerType = imported.profile.controllerType
            p.modes = imported.profile.modes
        }
        modeIndex = min(modeIndex, max(0, profile.modes.count - 1))
        rememberSheet(sheetID, syncedAt: nil)
        return imported.notes
    }

    func applyPush(_ result: PushResult) -> [String] {
        switch result {
        case let .pushed(sheetID, modifiedTime):
            rememberSheet(sheetID, syncedAt: modifiedTime)
            return []
        case let .keptOnline(sheetID, csv):
            return adoptOnlineVersion(csv, sheetID: sheetID)
        }
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
