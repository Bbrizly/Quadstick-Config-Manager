import Foundation

/// The UI's only door to storage. Today it is local JSON; later the
/// C# qsf bridge, a cable, or future hardware sits behind the same protocol.
public protocol ConfigurationRepository: Sendable {
    func loadProfiles() throws -> [Profile]
    func saveProfiles(_ profiles: [Profile]) throws
    func loadSettings() throws -> GlobalSettings
    func saveSettings(_ settings: GlobalSettings) throws
}

/// JSON files in Application Support, seeded with sample profiles on first
/// launch. Every mutation autosaves through this.
public final class MockConfigurationRepository: ConfigurationRepository, @unchecked Sendable {
    private let directory: URL
    private var profilesURL: URL { directory.appendingPathComponent("profiles.json") }
    private var settingsURL: URL { directory.appendingPathComponent("settings.json") }

    public init(directory: URL? = nil) {
        self.directory = directory
            ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("SipStudio", isDirectory: true)
        try? FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
    }

    public func loadProfiles() throws -> [Profile] {
        guard FileManager.default.fileExists(atPath: profilesURL.path) else {
            let seed = [SampleData.fortnite, SampleData.minecraft]
            try saveProfiles(seed)
            return seed
        }
        return try JSONDecoder().decode([Profile].self, from: Data(contentsOf: profilesURL))
    }

    public func saveProfiles(_ profiles: [Profile]) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(profiles).write(to: profilesURL, options: .atomic)
    }

    public func loadSettings() throws -> GlobalSettings {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else {
            return GlobalSettings()
        }
        return try JSONDecoder().decode(GlobalSettings.self, from: Data(contentsOf: settingsURL))
    }

    public func saveSettings(_ settings: GlobalSettings) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(settings).write(to: settingsURL, options: .atomic)
    }
}
