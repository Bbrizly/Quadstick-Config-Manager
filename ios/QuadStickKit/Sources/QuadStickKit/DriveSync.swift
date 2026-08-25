import Foundation

/// Putting a profile in Google Sheets and getting it back.
///
/// The desktop keeps its link in settings, keyed by the profile's file path,
/// which is why a rename there used to fork a second sheet. Here the link is
/// part of the profile, so it travels with it: into the device file's C1 on
/// export, into the app's own store on save, and back out of either one.
public enum ConflictChoice: Sendable, Equatable {
    /// Push anyway. Google keeps the online version in revision history.
    case replaceWithMine
    /// Leave the sheet alone and take its contents instead.
    case keepOnline
}

public enum PushResult: Equatable, Sendable {
    case pushed(sheetID: String, modifiedTime: String)
    /// Somebody edited the sheet online and the user chose to keep that. The
    /// CSV is the sheet's own contents, for the caller to import.
    case keptOnline(sheetID: String, csv: String)
}

public actor DriveSync {
    private let client: DriveClient

    public init(client: DriveClient) { self.client = client }

    /// Push a profile to its sheet, making one the first time.
    ///
    /// `resolve` is only ever called when the sheet changed online since this
    /// app last wrote it. Nothing is overwritten before it answers: a profile
    /// somebody edited on their laptop must not be silently replaced by a phone
    /// that has been asleep for a week.
    public func push(profile: Profile,
                     csv: String,
                     resolve: @Sendable () async -> ConflictChoice) async throws -> PushResult {
        let tabs = SheetTabs.split(csv: csv)

        guard let sheetID = profile.sheetID, !sheetID.isEmpty else {
            let id = try await client.createSpreadsheet(title: profile.name)
            try await client.push(tabs, to: id)
            return .pushed(sheetID: id, modifiedTime: try await client.modifiedTime(id))
        }

        let live = try await client.modifiedTime(sheetID)
        if let seen = profile.sheetSyncedTime, seen != live {
            if await resolve() == .keepOnline {
                return .keptOnline(sheetID: sheetID, csv: try await client.downloadProfileCSV(sheetID))
            }
        }
        try await client.push(tabs, to: sheetID)
        return .pushed(sheetID: sheetID, modifiedTime: try await client.modifiedTime(sheetID))
    }

    /// A link anyone can open, read-only. Pushes first, so the link never points
    /// at a sheet that is a version behind what the sender is looking at.
    public func shareLink(profile: Profile,
                          csv: String,
                          resolve: @Sendable () async -> ConflictChoice) async throws -> (url: URL, result: PushResult) {
        let result = try await push(profile: profile, csv: csv, resolve: resolve)
        let id: String
        switch result {
        case .pushed(let sheetID, _), .keptOnline(let sheetID, _): id = sheetID
        }
        try await client.shareAnyoneReader(id)
        return (DriveClient.editURL(id), result)
    }

    /// Every sheet this app made, newest first, so a new phone can pull a
    /// profile back without anybody pasting a link.
    public func mine() async throws -> [DriveSheetInfo] {
        try await client.listSpreadsheets().sorted { $0.modifiedTime > $1.modifiedTime }
    }

    public func download(_ id: String) async throws -> String {
        try await client.downloadProfileCSV(id)
    }

    /// Make a sheet for a profile whose old one was deleted. The caller offers
    /// this after a 404 rather than retrying a sheet that is not coming back.
    public func recreate(profile: Profile, csv: String) async throws -> PushResult {
        var fresh = profile
        fresh.sheetID = nil
        fresh.sheetSyncedTime = nil
        return try await push(profile: fresh, csv: csv, resolve: { .replaceWithMine })
    }
}
