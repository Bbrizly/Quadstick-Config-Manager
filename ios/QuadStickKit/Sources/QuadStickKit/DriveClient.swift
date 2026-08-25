import Foundation

/// Plain REST against Sheets and Drive, no Google SDK. The desktop app talks to
/// the same two endpoints in the same shapes, so a sheet either app writes is
/// one the other reads.
///
/// One deliberate difference. The desktop reads a sheet back by exporting the
/// whole workbook as .xlsx, because it already had a reader for that. A zip
/// reader on a phone is a lot of code to carry for one file, so this asks the
/// Sheets values API for the grid as JSON and rebuilds the CSV. Same result,
/// nothing to unzip.
public enum DriveError: Error, Equatable {
    case api(status: Int, message: String)
    /// The sheet is gone: deleted, or trashed, or never ours.
    case notFound
    case notAuthorized
    case badReply(String)
}

public struct DriveSheetInfo: Equatable, Sendable, Identifiable {
    public let id: String
    public let name: String
    public let modifiedTime: String
    public init(id: String, name: String, modifiedTime: String) {
        self.id = id
        self.name = name
        self.modifiedTime = modifiedTime
    }
}

public actor DriveClient {
    static let sheetsBase = "https://sheets.googleapis.com/v4/spreadsheets"
    static let driveBase = "https://www.googleapis.com/drive/v3/files"

    /// Matches the desktop's sweep. A profile past this many rows keeps a stale
    /// tail; nothing near it exists.
    static let lastRow = 10_000

    private let http: HTTPFetching
    private let token: @Sendable () async throws -> String

    public init(http: HTTPFetching = URLSessionHTTP(),
                token: @escaping @Sendable () async throws -> String) {
        self.http = http
        self.token = token
    }

    // MARK: - Create

    public func createSpreadsheet(title: String) async throws -> String {
        let json = try await send(post: Self.sheetsBase, body: ["properties": ["title": title]])
        guard let id = json["spreadsheetId"] as? String else {
            throw DriveError.badReply("no spreadsheetId in the reply")
        }
        return id
    }

    // MARK: - Write

    /// One worksheet tab per mode, which is how the community writes these and
    /// the only shape the importer reads back.
    ///
    /// Write first, clear the leftovers second, exactly like the desktop. A
    /// clear-then-write would leave the sheet blank if the write failed, and
    /// the sheet is often the only copy that is not on one phone.
    public func push(_ tabs: [SheetTab], to id: String) async throws {
        // An empty grid writes nothing, so the clear below would sweep the whole
        // sheet. A truncated local profile must never empty the backup.
        guard tabs.contains(where: { $0.rows.contains { $0.contains { !$0.trimmingCharacters(in: .whitespaces).isEmpty } } })
        else { return }

        try await shapeTabs(titles: tabs.map(\.title), in: id)

        var data: [[String: Any]] = []
        var ranges: [String] = []
        for tab in tabs {
            let width = max(1, tab.rows.map(\.count).max() ?? 1)
            // Every row padded to one width, so a binding that lost an input has
            // that cell blanked by the write instead of keeping its old value.
            let grid = tab.rows.map { $0 + Array(repeating: "", count: width - $0.count) }
            data.append(["range": "\(Self.quoted(tab.title))!A1", "values": grid])
            if grid.count < Self.lastRow {
                ranges.append("\(Self.quoted(tab.title))!A\(grid.count + 1):ZZ\(Self.lastRow)")
            }
            ranges.append("\(Self.quoted(tab.title))!\(Self.columnName(width + 1))1:ZZ\(Self.lastRow)")
        }

        // RAW so a cell that starts with "=" is stored as text, never run as a
        // formula.
        _ = try await send(post: "\(Self.sheetsBase)/\(id)/values:batchUpdate",
                           body: ["valueInputOption": "RAW", "data": data])
        if !ranges.isEmpty {
            _ = try await send(post: "\(Self.sheetsBase)/\(id)/values:batchClear",
                               body: ["ranges": ranges])
        }
    }

    /// Rename what is there, add what is missing, delete what a shorter profile
    /// left behind.
    ///
    /// Every existing tab is renamed out of the way first, in the same batch. A
    /// spreadsheet refuses two tabs with one title, so pushing a profile whose
    /// modes were reordered would otherwise collide with its own old names. One
    /// batchUpdate applies whole or not at all, so a failure cannot strand the
    /// placeholder names.
    private func shapeTabs(titles: [String], in id: String) async throws {
        let existing = try await listTabs(in: id)
        if existing.map(\.title) == titles { return }

        var requests: [[String: Any]] = []
        for (i, tab) in existing.enumerated() { requests.append(Self.rename(tab.sheetID, "_qsc_\(i)")) }
        for (i, title) in titles.enumerated() {
            if i < existing.count { requests.append(Self.rename(existing[i].sheetID, title)) }
            else { requests.append(["addSheet": ["properties": ["title": title]]]) }
        }
        for i in titles.count..<max(titles.count, existing.count) {
            requests.append(["deleteSheet": ["sheetId": existing[i].sheetID]])
        }
        _ = try await send(post: "\(Self.sheetsBase)/\(id):batchUpdate", body: ["requests": requests])
    }

    private static func rename(_ sheetID: Int, _ title: String) -> [String: Any] {
        ["updateSheetProperties": ["properties": ["sheetId": sheetID, "title": title], "fields": "title"]]
    }

    public func listTabs(in id: String) async throws -> [(sheetID: Int, title: String)] {
        let fields = "sheets.properties(sheetId,title)".addingPercentEncoding(
            withAllowedCharacters: .alphanumerics) ?? ""
        let json = try await send(get: "\(Self.sheetsBase)/\(id)?fields=\(fields)")
        let sheets = json["sheets"] as? [[String: Any]] ?? []
        return sheets.compactMap {
            guard let p = $0["properties"] as? [String: Any] else { return nil }
            return (p["sheetId"] as? Int ?? 0, p["title"] as? String ?? "")
        }
    }

    // MARK: - Read

    /// The whole workbook as device CSV, tabs in order with a blank line
    /// between them. That blank line is not decoration: the device ends a mode
    /// at an empty line and only looks for the next keyword after one.
    public func downloadProfileCSV(_ id: String) async throws -> String {
        let tabs = try await listTabs(in: id)
        guard !tabs.isEmpty else { return "" }

        var url = "\(Self.sheetsBase)/\(id)/values:batchGet?majorDimension=ROWS&valueRenderOption=FORMATTED_VALUE"
        for tab in tabs {
            let range = Self.quoted(tab.title)
            url += "&ranges=\(range.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? range)"
        }
        let json = try await send(get: url)
        let valueRanges = json["valueRanges"] as? [[String: Any]] ?? []

        var lines: [String] = []
        for (index, range) in valueRanges.enumerated() {
            if index > 0 { lines.append("") }
            let rows = range["values"] as? [[Any]] ?? []
            for row in rows {
                lines.append(DeviceFile.line(row.map { "\($0)" }).replacingOccurrences(of: "\r\n", with: ""))
            }
        }
        return lines.joined(separator: "\r\n") + "\r\n"
    }

    public func modifiedTime(_ id: String) async throws -> String {
        let json = try await send(get: "\(Self.driveBase)/\(id)?fields=modifiedTime")
        guard let time = json["modifiedTime"] as? String else {
            throw DriveError.badReply("no modifiedTime in the reply")
        }
        return time
    }

    /// Under drive.file this lists exactly the sheets this app made.
    public func listSpreadsheets() async throws -> [DriveSheetInfo] {
        var results: [DriveSheetInfo] = []
        let q = "mimeType='application/vnd.google-apps.spreadsheet' and trashed=false"
            .addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? ""
        var pageToken: String?
        repeat {
            var url = "\(Self.driveBase)?q=\(q)&fields=nextPageToken,files(id,name,modifiedTime)"
            if let pageToken { url += "&pageToken=\(pageToken)" }
            let json = try await send(get: url)
            for file in json["files"] as? [[String: Any]] ?? [] {
                guard let id = file["id"] as? String, let name = file["name"] as? String else { continue }
                results.append(DriveSheetInfo(id: id, name: name,
                                              modifiedTime: file["modifiedTime"] as? String ?? ""))
            }
            pageToken = json["nextPageToken"] as? String
        } while pageToken != nil
        return results
    }

    // MARK: - Share

    /// Anyone with the link may read. `allowFileDiscovery` false keeps it out of
    /// search, so it is link-only and not public.
    public func shareAnyoneReader(_ id: String) async throws {
        _ = try await send(post: "\(Self.driveBase)/\(id)/permissions",
                           body: ["role": "reader", "type": "anyone", "allowFileDiscovery": false])
    }

    public nonisolated static func editURL(_ id: String) -> URL {
        URL(string: "https://docs.google.com/spreadsheets/d/\(id)/edit?usp=sharing")!
    }

    // MARK: - Transport

    private func send(get url: String) async throws -> [String: Any] {
        var request = URLRequest(url: URL(string: url)!)
        request.httpMethod = "GET"
        return try await perform(request)
    }

    private func send(post url: String, body: Any) async throws -> [String: Any] {
        var request = URLRequest(url: URL(string: url)!)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await perform(request)
    }

    private func perform(_ request: URLRequest) async throws -> [String: Any] {
        var request = request
        request.setValue("Bearer \(try await token())", forHTTPHeaderField: "Authorization")
        let (data, response) = try await http.data(for: request)
        guard (200..<300).contains(response.statusCode) else {
            let message = String(decoding: data, as: UTF8.self)
            // 404 is its own case: the sheet was deleted or trashed, and the
            // caller offers to make a new one rather than retrying forever.
            if response.statusCode == 404 { throw DriveError.notFound }
            if response.statusCode == 401 || response.statusCode == 403 { throw DriveError.notAuthorized }
            throw DriveError.api(status: response.statusCode, message: message)
        }
        if data.isEmpty { return [:] }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
    }

    // MARK: - A1 notation

    /// A tab name in A1 notation is quoted, and an apostrophe inside it doubled.
    static func quoted(_ title: String) -> String {
        "'" + title.replacingOccurrences(of: "'", with: "''") + "'"
    }

    /// 1 is A, 27 is AA.
    static func columnName(_ index: Int) -> String {
        var index = index
        var name = ""
        while index > 0 {
            index -= 1
            name = String(UnicodeScalar(UInt8(65 + index % 26))) + name
            index /= 26
        }
        return name
    }
}
