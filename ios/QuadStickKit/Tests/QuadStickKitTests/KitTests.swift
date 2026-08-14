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
            XCTAssertNotNil(Firmware.outputKeyword[output.id], "no firmware keyword for \(output.id)")
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
        // The unknown output and the combo input are reported, never dropped quietly.
        XCTAssertTrue(result.notes.contains { $0.contains("ps4_authentication") }, "\(result.notes)")
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
