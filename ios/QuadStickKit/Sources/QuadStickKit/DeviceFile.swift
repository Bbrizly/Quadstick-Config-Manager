import Foundation

/// Reads and writes the CSV file the QuadStick itself loads from its flash
/// drive. Shape proved against the firmware's own reader (Configuration.c,
/// FW 2373, transcribed in the desktop repo's FirmwareOracle): a file starts
/// with a "QuadStick" line, each mode is a "Profile" segment of
/// output,function,input rows, segments end at a truly empty line, CRLF.
public enum DeviceFile {

    public struct ImportResult {
        public let profile: Profile
        public let notes: [String]
    }

    // MARK: - Export

    /// One CSV per profile, every mode inside, blank line between segments.
    /// Column layout matches the desktop app: A output, B function and
    /// parameters, C..J inputs, K notes, L this app's name for the action.
    public static func export(_ profile: Profile,
                              fileName: String? = nil,
                              makeDefault: Bool = false,
                              capabilities: DeviceCapabilities = QuadStickCatalog.capabilities) -> String {
        let base = makeDefault ? "default" : sanitizedFileName(fileName ?? profile.name)
        var rows: [[String]] = []
        rows.append(["QuadStick Configuration", "Version 1.5", profile.sheetID ?? "", profile.name])

        for (index, mode) in profile.modes.enumerated() {
            rows.append([])   // firmware needs a truly empty line before each segment
            rows.append(["Profile Name", "", mode.name])
            rows.append(["\(base).csv"])
            rows.append(["Output or Function", "Function", "usb"])

            if index == 0 && profile.controllerType != .standard {
                // Per-mode preference override row: B is skipped by the
                // firmware, C carries the value.
                rows.append(["enable_DS3_emulation", "", "\(profile.controllerType.firmwareMode)"])
            }

            // Catalog order keeps the file stable between saves.
            for input in capabilities.inputs {
                for action in input.actions {
                    guard let assignment = mode.assignments[action.id],
                          let output = assignment.output,
                          let outputWord = Firmware.keyword(forOutput: output.id),
                          let inputWord = Firmware.inputKeyword[action.id] else { continue }
                    var row = [outputWord, functionCell(assignment.function), inputWord]
                    if let label = assignment.label, !label.isEmpty {
                        while row.count < 11 { row.append("") }
                        row.append(label)   // column L, ignored by the device
                    }
                    rows.append(row)
                }
            }
        }
        rows.append([])   // terminate the last segment
        return rows.map { line($0) }.joined()
    }

    /// Millisecond and percent values go out exactly as stored. The repeat
    /// parameter is the period in ms (DataFlow.c halves it for the on time).
    static func functionCell(_ f: InputFunction?) -> String {
        switch f {
        case nil: "normal"
        case .toggle: "toggle"
        case .repeatWhileHeld(let ms): "repeat \(ms)"
        case .greaterThan(let pct): "greater_than \(pct)"
        case .delayedLatch(let ms): "delayed_latch \(ms)"
        }
    }

    /// The device cannot open a name longer than 31 characters with ".csv",
    /// and its reader splits cells on anything outside alnum, "_. -".
    public static func sanitizedFileName(_ name: String) -> String {
        let allowed = name.filter { $0.isASCII && ($0.isLetter || $0.isNumber || "_. -".contains($0)) }
        let trimmed = allowed.trimmingCharacters(in: .whitespaces)
        let capped = String(trimmed.prefix(27))
        return capped.isEmpty ? "profile" : capped
    }

    static func line(_ cells: [String]) -> String {
        cells.map { cell in
            cell.contains(where: { ",\"\n\r".contains($0) })
                ? "\"\(cell.replacingOccurrences(of: "\"", with: "\"\""))\""
                : cell
        }.joined(separator: ",") + "\r\n"
    }

    // MARK: - Text encoding

    /// The device writes and reads its files as CP437, so a file that came off
    /// a QuadStick is not always valid UTF-8. Decoding it as UTF-8 anyway turns
    /// every accented character into U+FFFD and the import still calls itself
    /// clean, which is the one thing an import here must never do.
    public static let deviceEncoding = String.Encoding(
        rawValue: CFStringConvertEncodingToNSStringEncoding(
            CFStringEncoding(CFStringEncodings.dosLatinUS.rawValue)))

    /// Text, plus a note when it did not arrive as UTF-8. The note is shown,
    /// never swallowed.
    public static func decode(_ data: Data) -> (text: String, note: String?) {
        if let utf8 = String(data: data, encoding: .utf8) {
            return (utf8, nil)
        }
        if let cp437 = String(data: data, encoding: deviceEncoding) {
            return (cp437, "This file is not UTF-8 text, so it was read as CP437, the QuadStick's own encoding. Check any name with an accent in it.")
        }
        return (String(decoding: data, as: UTF8.self),
                "Some characters in this file could not be read and were replaced. Check the names.")
    }

    /// Whether the device can show this name as written. Anything outside CP437
    /// makes it fall back to the mangled 8.3 name on the device's own screen.
    public static func isDeviceReadable(_ name: String) -> Bool {
        name.data(using: deviceEncoding, allowLossyConversion: false) != nil
    }

    // MARK: - Import

    /// Accepts a full device file or a single Google Sheets tab (one mode).
    /// Anything it cannot represent is skipped and reported in notes, never
    /// silently dropped.
    public static func importProfile(csv text: String, fallbackName: String) -> ImportResult? {
        let lines = text.components(separatedBy: "\n")
            .map { $0.hasSuffix("\r") ? String($0.dropLast()) : $0 }

        var modes: [Mode] = []
        var notes: [String] = []
        var profileName: String?
        var controllerType = ControllerType.standard
        var sheetID: String?
        var at = 0

        // Header line, when there is one: "QuadStick Configuration,Version
        // 1.5,<sheet id>,<name>". Dropping C1 would cut a desktop profile off
        // from its own Google Sheet, so it is read here and written back out.
        let head = parseLine(lines.first ?? "")
        if (head.first ?? "").hasPrefix("QuadStick") {
            let cell = head.count > 2 ? head[2].trimmingCharacters(in: .whitespaces) : ""
            if !cell.isEmpty { sheetID = cell }
        }

        while at < lines.count {
            let cells = parseLine(lines[at])
            let first = cells.first ?? ""
            if first.hasPrefix("Profile") {
                at += 1
                // Filename row: the profile's own name, first segment only.
                if at < lines.count {
                    let fileCell = parseLine(lines[at]).first ?? ""
                    if profileName == nil, !fileCell.isEmpty {
                        profileName = fileCell.hasSuffix(".csv") ? String(fileCell.dropLast(4)) : fileCell
                    }
                    at += 1
                }
                at += 1   // labels row
                let modeName = cells.count > 2 && !cells[2].isEmpty ? cells[2] : "Mode \(modes.count + 1)"
                var mode = Mode(name: modeName)
                while at < lines.count && !lines[at].isEmpty {
                    readBinding(parseLine(lines[at]), into: &mode,
                                controllerType: &controllerType, notes: &notes)
                    at += 1
                }
                modes.append(mode)
            } else if first.hasPrefix("Preferences") || first.hasPrefix("Infrared") {
                at += 3
                while at < lines.count && !lines[at].isEmpty { at += 1 }
                notes.append("The file's \(first.hasPrefix("Pref") ? "Preferences" : "Infrared") section stays on the QuadStick and is not edited here.")
            } else {
                at += 1
            }
        }

        guard !modes.isEmpty else { return nil }
        if modes.count > 16 {
            notes.append("The file has \(modes.count) modes. The QuadStick only loads the first 16.")
        }
        let profile = Profile(name: profileName ?? fallbackName,
                              controllerType: controllerType, modes: modes, sheetID: sheetID)
        return ImportResult(profile: profile, notes: notes)
    }

    private static func readBinding(_ cells: [String], into mode: inout Mode,
                                    controllerType: inout ControllerType,
                                    notes: inout [String]) {
        guard let keyword = cells.first?.trimmingCharacters(in: .whitespaces), !keyword.isEmpty else { return }

        if keyword == "enable_DS3_emulation" {
            let value = Int(cells.count > 2 ? cells[2].trimmingCharacters(in: .whitespaces) : "") ?? 0
            if let type = ControllerType.allCases.first(where: { $0.firmwareMode == value }) {
                controllerType = type
            } else {
                notes.append("Controller setting \(value) is not one this app names. Kept as Default.")
            }
            return
        }

        guard let outputID = Firmware.outputID(forKeyword: keyword),
              let output = QuadStickCatalog.output(outputID) else {
            let inputsPresent = cells.dropFirst(2).prefix(8).contains { !$0.isEmpty }
            if inputsPresent {
                notes.append("\(keyword) is not an action this app lists yet. That row was left out.")
            }
            return
        }

        let inputWords = cells.dropFirst(2).prefix(8)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && $0 != "none" }
        guard !inputWords.isEmpty else { return }   // placeholder row, nothing assigned
        guard inputWords.count == 1 else {
            notes.append("\(output.name) uses a sequence of \(inputWords.count) inputs, which this app cannot edit yet. That row was left out.")
            return
        }
        guard let actionID = Firmware.actionID[inputWords[0]] else {
            notes.append("\(output.name) is triggered by \(inputWords[0]), which this app does not show yet. That row was left out.")
            return
        }

        guard let function = parseFunction(cells.count > 1 ? cells[1] : "", output: output.name, notes: &notes) else {
            return   // unrepresentable behavior, already noted
        }

        if mode.assignments[actionID] != nil {
            notes.append("Two rows assign the same input in \(mode.name). The later one was kept.")
        }
        let label = cells.count > 11 && !cells[11].isEmpty ? cells[11] : nil
        mode.assignments[actionID] = Assignment(output: output, label: label, function: function)
    }

    /// Returns .some(function) when representable, .some(nil) for normal,
    /// nil when the row must be skipped.
    private static func parseFunction(_ cell: String, output: String,
                                      notes: inout [String]) -> InputFunction?? {
        let words = cell.trimmingCharacters(in: .whitespaces)
            .split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        let name = words.first ?? ""
        let param = words.count > 1 ? Int(words[1]) : nil
        if words.count > 2 {
            // A second parameter (repeat delay, greater_than off point) would
            // be lost on the next save, so the row is kept out instead.
            notes.append("\(output) uses \(cell), which has a setting this app cannot keep. That row was left out.")
            return nil
        }
        switch name {
        // A missing parameter is 0 to the firmware (atoi), kept as 0 here.
        case "", "normal": return .some(nil)
        case "toggle": return .some(.toggle)
        case "repeat": return .some(.repeatWhileHeld(intervalMS: param ?? 0))
        case "greater_than": return .some(.greaterThan(percent: param ?? 0))
        case "delayed_latch": return .some(.delayedLatch(delayMS: param ?? 0))
        default:
            notes.append("\(output) uses the \(name) behavior, which this app cannot edit yet. That row was left out.")
            return nil
        }
    }

    /// Quote-aware split of one CSV line. Cells never span lines here; the
    /// desktop app flattens embedded newlines before a file reaches a device.
    static func parseLine(_ line: String) -> [String] {
        var cells: [String] = []
        var cell = ""
        var inQuotes = false
        var chars = line.makeIterator()
        while let c = chars.next() {
            if inQuotes {
                if c == "\"" {
                    // Peek for an escaped quote by buffering manually.
                    if let next = chars.next() {
                        if next == "\"" { cell.append("\"") } else {
                            inQuotes = false
                            if next == "," { cells.append(cell); cell = "" } else { cell.append(next) }
                        }
                    } else {
                        inQuotes = false
                    }
                } else {
                    cell.append(c)
                }
            } else if c == "\"" && cell.isEmpty {
                inQuotes = true
            } else if c == "," {
                cells.append(cell); cell = ""
            } else {
                cell.append(c)
            }
        }
        cells.append(cell)
        return cells
    }
}

/// Turns a pasted Google Sheets link into the anonymous CSV export URL, the
/// same endpoints the desktop app uses. The sheet must be link shared.
public enum SheetsLink {
    public static func csvExportURL(from pasted: String) -> URL? {
        let text = pasted.trimmingCharacters(in: .whitespacesAndNewlines)
        let gid = capture(#"[#?&]gid=(\d+)"#, in: text)

        // Published links come first: their id also matches the plain pattern.
        if let pub = capture(#"/spreadsheets/d/e/([A-Za-z0-9_-]{20,})"#, in: text) {
            var url = "https://docs.google.com/spreadsheets/d/e/\(pub)/pub?output=csv"
            if let gid { url += "&gid=\(gid)" }
            return URL(string: url)
        }
        let id = capture(#"/spreadsheets/d/([A-Za-z0-9_-]{20,})"#, in: text)
            ?? capture(#"[?&]key=([A-Za-z0-9_-]{20,})"#, in: text)
        guard let id else { return nil }
        var url = "https://docs.google.com/spreadsheets/d/\(id)/export?format=csv"
        if let gid { url += "&gid=\(gid)" }
        return URL(string: url)
    }

    private static func capture(_ pattern: String, in text: String) -> String? {
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let range = Range(match.range(at: 1), in: text) else { return nil }
        return String(text[range])
    }
}
