import Foundation

/// One worksheet tab: the title it is given and the rows on it.
public struct SheetTab: Equatable, Sendable {
    public let title: String
    public let rows: [[String]]
    public init(title: String, rows: [[String]]) {
        self.title = title
        self.rows = rows
    }
}

/// A device CSV split the way the community writes a workbook: one tab per
/// sheet, titled with the mode name. The desktop's SheetTabs does the same
/// split from the same rules, so a sheet either app pushes is one the other
/// reads without noticing which wrote it.
///
/// The device never sees a tab name and the CSV has nowhere to put one, so this
/// is presentation only. It matters because pushing the file flat onto one
/// worksheet is a shape neither the community sheets nor either importer
/// expect.
public enum SheetTabs {
    /// Google's own cap on a worksheet title.
    static let maxTitle = 100

    public static func split(csv text: String) -> [SheetTab] {
        let lines = text.components(separatedBy: "\n")
            .map { $0.hasSuffix("\r") ? String($0.dropLast()) : $0 }
        let grid = lines.map { DeviceFile.parseLine($0) }

        // Where each sheet starts: a row whose first cell is a device keyword.
        var starts: [Int] = []
        for (i, row) in grid.enumerated() {
            let first = (row.first ?? "").trimmingCharacters(in: .whitespaces)
            if first.contains("Profile") || first == "Preferences" || first == "Infrared" {
                starts.append(i)
            }
        }

        // Nothing recognised. Push it whole rather than decide it is not worth
        // keeping: the sheet may be the only copy that is not on this phone.
        guard !starts.isEmpty else {
            return [SheetTab(title: "Profile", rows: trimmed(Array(grid)))]
        }

        var tabs: [SheetTab] = []
        var taken = Set<String>()
        for (n, start) in starts.enumerated() {
            // The first tab starts at row 0, not at its keyword row, so the
            // version header above it (the sheet id and the profile's name)
            // travels with the profile instead of being dropped.
            let from = n == 0 ? 0 : start - 1
            let to = n + 1 < starts.count ? starts[n + 1] - 1 : grid.count
            guard from < to else { continue }
            let rows = trimmed(Array(grid[from..<to]))
            let keyword = (grid[start].first ?? "").trimmingCharacters(in: .whitespaces)
            tabs.append(SheetTab(title: unique(title(keyword, grid[start], n), &taken), rows: rows))
        }
        return tabs
    }

    // The blank row between two sheets belongs to neither of them: the device
    // needs it to end a mode, and a reader puts one back between tabs.
    private static func trimmed(_ rows: [[String]]) -> [[String]] {
        var rows = rows
        while let last = rows.last, last.allSatisfy({ $0.trimmingCharacters(in: .whitespaces).isEmpty }) {
            rows.removeLast()
        }
        return rows
    }

    private static func title(_ keyword: String, _ row: [String], _ index: Int) -> String {
        if keyword == "Preferences" { return "Preferences" }
        if keyword == "Infrared" { return "Infrared" }
        let name = row.count > 2 ? row[2].trimmingCharacters(in: .whitespaces) : ""
        return name.isEmpty ? "Mode \(index + 1)" : safe(name)
    }

    /// Sheets rejects these characters in a title and cuts one past 100.
    static func safe(_ name: String) -> String {
        let cleaned = String(name.filter { !$0.isASCII || !"[]:*?/\\".contains($0) }
            .filter { !$0.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) } })
            .trimmingCharacters(in: .whitespaces)
        if cleaned.isEmpty { return "Mode" }
        return cleaned.count <= maxTitle ? cleaned : String(cleaned.prefix(maxTitle)).trimmingCharacters(in: .whitespaces)
    }

    /// Two modes may share a name, because the device tells them apart by
    /// position. Two tabs in one spreadsheet may not.
    static func unique(_ title: String, _ taken: inout Set<String>) -> String {
        if taken.insert(title).inserted { return title }
        var n = 2
        while true {
            var candidate = "\(title) (\(n))"
            if candidate.count > maxTitle {
                candidate = "\(String(title.prefix(maxTitle - 5)).trimmingCharacters(in: .whitespaces)) (\(n))"
            }
            if taken.insert(candidate).inserted { return candidate }
            n += 1
        }
    }
}
