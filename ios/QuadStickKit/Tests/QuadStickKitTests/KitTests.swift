import XCTest
@testable import QuadStickKit

final class KitTests: XCTestCase {

    func testCatalogActionsResolve() {
        let caps = QuadStickCatalog.capabilities
        XCTAssertEqual(caps.inputs.filter { $0.face == .front }.count, 6)
        XCTAssertEqual(caps.inputs.filter { $0.face == .back }.count, 4)
        XCTAssertEqual(caps.input(forAction: "left-tube-soft-sip")?.id, "left-tube")
        XCTAssertEqual(caps.action("usb-up")?.fullName, "USB Joystick Up")
    }

    func testSampleProfileOutputsExistInCatalog() {
        for mode in SampleData.fortnite.modes {
            for (actionID, a) in mode.assignments {
                XCTAssertNotNil(QuadStickCatalog.capabilities.action(actionID),
                                "unknown input action \(actionID)")
                if let out = a.output {
                    XCTAssertNotNil(QuadStickCatalog.output(out.id), "unknown output \(out.id)")
                }
            }
        }
    }

    func testProfileRoundTripsThroughRepository() throws {
        let dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("kit-tests-\(UUID().uuidString)")
        let repo = MockConfigurationRepository(directory: dir)
        let seeded = try repo.loadProfiles()
        XCTAssertEqual(seeded.first?.name, "Fortnite")

        var edited = seeded
        edited[0].modes[0].assignments["left-tube-soft-sip"] =
            Assignment(output: QuadStickCatalog.output("controller-b"), label: "Emote")
        try repo.saveProfiles(edited)
        let reloaded = try repo.loadProfiles()
        XCTAssertEqual(reloaded, edited)
    }

    func testValidatorFlagsImpossibleFunctionValues() {
        var p = SampleData.fortnite
        p.modes[0].assignments["right-tube-soft-sip"] =
            Assignment(output: QuadStickCatalog.output("controller-a"),
                       function: .greaterThan(percent: 1000))
        let issues = ConfigValidator.validate(p, capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.contains { $0.severity == .error && $0.message.contains("1000%") })
    }

    func testValidatorExplainsDuplicateModeNames() {
        var p = SampleData.fortnite
        p.modes[2].name = "Movement"
        let issues = ConfigValidator.validate(p, capabilities: QuadStickCatalog.capabilities)
        let dup = issues.first { $0.id.hasPrefix("dup-name") }
        XCTAssertEqual(dup?.severity, .warning)
        XCTAssertTrue(dup?.message.contains("by number") ?? false)
    }

    func testValidatorWarnsOnFunctionWithoutOutput() {
        var p = SampleData.fortnite
        p.modes[0].assignments["center-tube-soft-sip"] =
            Assignment(function: .toggle)
        let issues = ConfigValidator.validate(p, capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.contains { $0.id.hasPrefix("fn-noout") && $0.severity == .warning })
    }

    func testCleanSampleHasNoErrors() {
        let issues = ConfigValidator.validate(SampleData.fortnite,
                                              capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.allSatisfy { $0.severity != .error }, "\(issues)")
    }

    func testSecondsFormatting() {
        XCTAssertEqual(InputFunction.seconds(1000), "1 second")
        XCTAssertEqual(InputFunction.seconds(500), "0.5 seconds")
        XCTAssertEqual(InputFunction.seconds(2000), "2 seconds")
    }

    // MARK: - Device file codec

    func testEveryCatalogEntryHasAFirmwareKeyword() {
        for output in QuadStickCatalog.outputs {
            XCTAssertNotNil(Firmware.keyword(forOutput: output.id), "no firmware keyword for \(output.id)")
        }
        for input in QuadStickCatalog.capabilities.inputs {
            for action in input.actions {
                XCTAssertNotNil(Firmware.inputKeyword[action.id], "no firmware keyword for \(action.id)")
            }
        }
    }

    func testExportShapeMatchesTheFirmwareReader() {
        let csv = DeviceFile.export(SampleData.fortnite, makeDefault: true)
        let lines = csv.components(separatedBy: "\r\n")
        XCTAssertTrue(lines[0].hasPrefix("QuadStick"), "firmware bails out without a QuadStick first line")
        XCTAssertEqual(lines.filter { $0.hasPrefix("Profile Name") }.count, 4)
        // Every segment keyword row follows a truly empty line.
        for (i, line) in lines.enumerated() where line.hasPrefix("Profile Name") {
            XCTAssertEqual(lines[i - 1], "", "segment at line \(i) not preceded by an empty line")
        }
        XCTAssertTrue(lines.contains("enable_DS3_emulation,,1"), "PlayStation profile writes the emulation override")
        XCTAssertTrue(lines.contains("A,normal,mp_left_sip,,,,,,,,,Jump"), "\(lines.prefix(12))")
        XCTAssertTrue(lines.contains { $0.hasPrefix("Y,delayed_latch 500,mp_right_puff") })
        XCTAssertTrue(lines.contains("increment_mode,normal,lip"))
    }

    func testExportImportRoundTrip() throws {
        let original = SampleData.fortnite
        let csv = DeviceFile.export(original)
        let result = try XCTUnwrap(DeviceFile.importProfile(csv: csv, fallbackName: "x"))
        XCTAssertEqual(result.profile.name, "Fortnite")
        XCTAssertEqual(result.profile.controllerType, .playstation)
        XCTAssertEqual(result.profile.modes.count, 4)
        for (a, b) in zip(original.modes, result.profile.modes) {
            XCTAssertEqual(a.name, b.name)
            XCTAssertEqual(a.assignments, b.assignments, "mode \(a.name)")
        }
        XCTAssertTrue(result.notes.isEmpty, "\(result.notes)")
    }

    func testImportReadsARealDeviceStyleFile() throws {
        let csv = """
        Profile Name,,Left joy,,,,,,,\r
        gta.csv,,Normal,,,,,,,\r
        Output or Function,Function,usb,,,,,,,\r
        increment_mode,normal,right_sip,,,,,,,\r
        left_1,normal,mp_left_sip,,,,,,,\r
        x,toggle,mp_center_puff,,,,,,,\r
        left_joy_up,normal,up,,,,,,,\r
        dpad_N,normal,,,,,,,,\r
        ps4_authentication,normal,mp_triple_sip,,,,,,,\r
        """
        let result = try XCTUnwrap(DeviceFile.importProfile(csv: csv, fallbackName: "x"))
        XCTAssertEqual(result.profile.name, "gta")
        let mode = result.profile.modes[0]
        XCTAssertEqual(mode.name, "Left joy")
        XCTAssertEqual(mode.assignments["side-tube-normal-sip"]?.output?.id, "mode & profile-next-mode")
        XCTAssertEqual(mode.assignments["left-tube-normal-sip"]?.output?.id, "controller-left-bumper")
        XCTAssertEqual(mode.assignments["center-tube-normal-puff"]?.output?.id, "controller-a")
        XCTAssertEqual(mode.assignments["center-tube-normal-puff"]?.function, .toggle)
        XCTAssertEqual(mode.assignments["joystick-up"]?.output?.id, "controller-left-stick-up")
        // mp_triple_sip is a real firmware input this app has no control for
        // yet. The row is reported, never dropped quietly.
        XCTAssertTrue(result.notes.contains { $0.contains("mp_triple_sip") }, "\(result.notes)")
    }

    func testImportRejectsNonProfileText() {
        XCTAssertNil(DeviceFile.importProfile(csv: "<html>sign in</html>", fallbackName: "x"))
        XCTAssertNil(DeviceFile.importProfile(csv: "just,some,cells", fallbackName: "x"))
    }

    func testSanitizedFileNameFitsTheDevice() {
        XCTAssertEqual(DeviceFile.sanitizedFileName("Fortnite"), "Fortnite")
        XCTAssertEqual(DeviceFile.sanitizedFileName("A/B:C?"), "ABC")
        XCTAssertLessThanOrEqual(DeviceFile.sanitizedFileName(String(repeating: "x", count: 60)).count + 4, 31)
        XCTAssertEqual(DeviceFile.sanitizedFileName("///"), "profile")
    }

    func testSheetsLinkParsing() {
        let id = String(repeating: "a", count: 30)
        XCTAssertEqual(
            SheetsLink.csvExportURL(from: "https://docs.google.com/spreadsheets/d/\(id)/edit#gid=123")?.absoluteString,
            "https://docs.google.com/spreadsheets/d/\(id)/export?format=csv&gid=123")
        XCTAssertEqual(
            SheetsLink.csvExportURL(from: "https://docs.google.com/spreadsheets/d/e/\(id)/pubhtml")?.absoluteString,
            "https://docs.google.com/spreadsheets/d/e/\(id)/pub?output=csv")
        XCTAssertNil(SheetsLink.csvExportURL(from: "https://example.com/not-a-sheet"))
    }
}

// MARK: - Firmware vocabulary

extension KitTests {

    /// The kit ships a copy of the desktop's validation.json. A copy that
    /// falls behind is how the two apps start disagreeing about what the
    /// device accepts, so the copy is compared to the original whenever the
    /// desktop tree is on disk.
    func testTheVocabularyCopyMatchesTheDesktop() throws {
        let desktop = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // QuadStickKitTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // QuadStickKit
            .deletingLastPathComponent()   // ios
            .deletingLastPathComponent()   // repo root
            .appendingPathComponent("src/QuadStick.Format/Data/validation.json")
        guard let original = try? Data(contentsOf: desktop) else {
            throw XCTSkip("desktop tree not present; nothing to compare against")
        }
        let shipped = try Data(contentsOf: XCTUnwrap(
            Bundle.module.url(forResource: "validation", withExtension: "json")))
        XCTAssertEqual(shipped, original,
                       "ios/QuadStickKit/Sources/QuadStickKit/Resources/validation.json is stale; copy it again")
    }

    func testTheCatalogOffersTheWholeFirmwareVocabulary() {
        let offered = Set(QuadStickCatalog.outputs.compactMap { Firmware.keyword(forOutput: $0.id) })
        let missing = Vocabulary.outputNames.filter { $0 != "none" && !offered.contains($0) }
        XCTAssertEqual(missing, [], "firmware words the editor cannot pick")
        XCTAssertGreaterThan(QuadStickCatalog.outputs.count, 380)
    }

    func testInfraredAndKeyboardWordsAreReachable() {
        let byKeyword = Dictionary(
            QuadStickCatalog.outputs.compactMap { o in
                Firmware.keyword(forOutput: o.id).map { ($0, o) }
            },
            uniquingKeysWith: { first, _ in first })
        XCTAssertEqual(byKeyword["ir_tv_volume_up"]?.category, .infrared)
        XCTAssertEqual(byKeyword["ir_tv_volume_up"]?.name, "TV Volume Up")
        XCTAssertEqual(byKeyword["kb_left_shift"]?.category, .keyboard)
        XCTAssertEqual(byKeyword["gyroscope_x_cw"]?.category, .motion)
        XCTAssertEqual(byKeyword["digital_out1_on"]?.category, .quadstick)
        // Read aloud, "Dpad" and "Out1" come out wrong.
        XCTAssertEqual(byKeyword["dpad_NE"]?.name, "D-pad NE")
        XCTAssertEqual(byKeyword["digital_out1_toggle"]?.name, "Digital Out 1 Toggle")
    }

    /// Round trip through the codec: a word only the firmware list knows must
    /// survive export and come back unchanged.
    func testAnUncuratedWordSurvivesASaveAndReload() throws {
        let ir = try XCTUnwrap(QuadStickCatalog.output("ir_tv_on_off"))
        let profile = Profile(name: "TV", modes: [
            Mode(name: "Watch", assignments: ["lip-press": Assignment(output: ir, label: "TV power")])
        ])
        let csv = DeviceFile.export(profile, makeDefault: false)
        XCTAssertTrue(csv.contains("ir_tv_on_off"), "the firmware word was not written")
        let read = try XCTUnwrap(DeviceFile.importProfile(csv: csv, fallbackName: "TV"))
        XCTAssertEqual(read.profile.modes.first?.assignments["lip-press"]?.output?.id, "ir_tv_on_off")
        XCTAssertEqual(read.notes, [], "a word the firmware lists should import without complaint")
    }

    func testAWordTheAppDoesNotKnowIsAWarningNotAnError() {
        let invented = OutputAction(id: "not_a_firmware_word", name: "Nonsense", category: .controller)
        let profile = Profile(name: "Odd", modes: [
            Mode(name: "One", assignments: ["lip-press": Assignment(output: invented)])
        ])
        let issues = ConfigValidator.validate(profile, capabilities: QuadStickCatalog.capabilities)
        let unknown = issues.filter { $0.id.hasPrefix("unknown-out-") }
        XCTAssertEqual(unknown.count, 1)
        XCTAssertEqual(unknown.first?.severity, .warning, "the device is the judge, not this app")
    }
}

// MARK: - Device photo regions
//
// Per-model photo geometry (three photos, three sets of hotspots) is pinned
// in DeviceModelTests.swift, which replaced the single shared table these
// tests used to check.

extension KitTests {

    // MARK: - Names the device can show

    /// A name outside CP437 makes the QuadStick fall back to the mangled 8.3
    /// name on its own screen, so the app has to say so before install.
    func testProfileNameOutsideCP437IsFlaggedButNotBlocked() {
        let profile = Profile(name: "Zelda \u{1F3AE}", controllerType: .standard,
                              modes: [Mode(name: "Mode 1", assignments: [:])])
        let issues = ConfigValidator.validate(profile, capabilities: QuadStickCatalog.capabilities)
        let encoding = issues.filter { $0.id == "profile-name-encoding" }
        XCTAssertEqual(encoding.count, 1, "an unshowable profile name must be reported")
        XCTAssertEqual(encoding.first?.severity, .warning, "the device may still load it, so never an error")
    }

    /// CP437 carries the accented Latin letters, so flagging them would be a
    /// false alarm telling people to rename a profile that was always fine.
    func testAccentedNameIsNotFlaggedBecauseCP437HasIt() {
        let profile = Profile(name: "Caf\u{00E9}", controllerType: .standard,
                              modes: [Mode(name: "Movement", assignments: [:])])
        let issues = ConfigValidator.validate(profile, capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.allSatisfy { !$0.id.contains("encoding") })
    }

    func testPlainNameIsNotFlagged() {
        let profile = Profile(name: "Fortnite", controllerType: .standard,
                              modes: [Mode(name: "Movement", assignments: [:])])
        let issues = ConfigValidator.validate(profile, capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.allSatisfy { !$0.id.contains("encoding") })
    }

    func testModeNameOutsideCP437IsFlagged() {
        let profile = Profile(name: "Game", controllerType: .standard,
                              modes: [Mode(name: "\u{6B69}\u{304F}", assignments: [:])])
        let issues = ConfigValidator.validate(profile, capabilities: QuadStickCatalog.capabilities)
        XCTAssertTrue(issues.contains { $0.id.hasPrefix("mode-name-encoding") })
    }

    /// CP437 is a byte-per-character encoding, so a file off the device is not
    /// always valid UTF-8. Decoding it as UTF-8 anyway silently replaces the
    /// character and the import still calls itself clean.
    func testCP437FileIsReadAsCP437AndSaysSo() {
        // 0x82 is e-acute in CP437 and is not valid UTF-8 on its own.
        let data = Data([0x43, 0x61, 0x66, 0x82])
        let (text, note) = DeviceFile.decode(data)
        XCTAssertEqual(text, "Caf\u{00E9}")
        XCTAssertNotNil(note, "an encoding fallback must be reported, never silent")
    }

    func testUTF8FileIsReadAsUTF8WithNoNote() {
        let (text, note) = DeviceFile.decode(Data("Caf\u{00E9}".utf8))
        XCTAssertEqual(text, "Caf\u{00E9}")
        XCTAssertNil(note)
    }

}

// MARK: - The Google Sheet a profile came from

/// Cell C1 is the sheet id. The firmware compares only the first nine
/// characters of that line, so carrying it costs the device nothing, and
/// dropping it would cut a desktop profile off from its own backup sheet.
final class SheetIdentityTests: XCTestCase {
    private let id = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"

    private func roundTrip(_ csv: String) -> Profile? {
        DeviceFile.importProfile(csv: csv, fallbackName: "Fallback")?.profile
    }

    func testSheetIdSurvivesImportThenExport() throws {
        let original = DeviceFile.export(Profile(name: "Rocket League",
                                                 modes: [Mode(name: "Driving")],
                                                 sheetID: id))
        let reimported = try XCTUnwrap(roundTrip(original))
        XCTAssertEqual(reimported.sheetID, id)
        XCTAssertTrue(DeviceFile.export(reimported).contains(id))
    }

    func testHeaderWithNoSheetIdStaysEmpty() throws {
        let profile = try XCTUnwrap(roundTrip(DeviceFile.export(
            Profile(name: "Plain", modes: [Mode(name: "One")]))))
        XCTAssertNil(profile.sheetID)
        // An absent id writes a blank cell, not the string "nil".
        XCTAssertTrue(DeviceFile.export(profile)
            .hasPrefix("QuadStick Configuration,Version 1.5,,Plain"))
    }

    func testFileWithNoHeaderAtAllStillImports() throws {
        let full = DeviceFile.export(Profile(name: "Head less", modes: [Mode(name: "One")]))
        let headless = full.components(separatedBy: "\n").dropFirst().joined(separator: "\n")
        let profile = try XCTUnwrap(roundTrip(headless))
        XCTAssertNil(profile.sheetID)
        XCTAssertFalse(profile.modes.isEmpty)
    }
}
