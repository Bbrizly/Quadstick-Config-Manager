import XCTest
@testable import QuadStickKit

final class OutputVisualTests: XCTestCase {

    // ponytail: find the asset catalog from this file's own path instead of
    // hardcoding a repo-root guess, so the test still finds it when run from
    // a different working directory.
    private var assetCatalogURL: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent() // OutputVisualTests.swift -> QuadStickKitTests
            .deletingLastPathComponent() // -> Tests
            .deletingLastPathComponent() // -> QuadStickKit
            .deletingLastPathComponent() // -> ios
            .appendingPathComponent("App/Assets.xcassets")
    }

    private let styles: [ControllerPromptStyle] = [.playstation, .xbox]

    // The most valuable test here: a typo'd asset name is silent everywhere
    // else, because a missing imageset just fails to draw. Sweep the
    // resolver's whole output space and prove every asset key it can emit
    // reaches a real imageset with a PNG in it, and that every output gets a
    // readable label regardless of whether it has art.
    func testEveryResolvedAssetNameExistsOnDiskAndEveryLabelIsNonEmpty() {
        let catalog = assetCatalogURL
        var checked = 0
        for style in styles {
            for output in QuadStickCatalog.outputs {
                let visual = OutputVisual.for(output, promptStyle: style)
                XCTAssertFalse(visual.friendlyLabel.isEmpty, output.id)
                guard let key = visual.assetKey else { continue }
                guard let art = OutputVisual.assetPath(for: key) else {
                    XCTFail("\(output.id) -> \(key) has no imageset mapping")
                    continue
                }
                let dir = catalog.appendingPathComponent("\(art.name).imageset")
                let png = dir.appendingPathComponent("\(art.name).png")
                XCTAssertTrue(FileManager.default.fileExists(atPath: dir.path),
                              "\(output.id) -> \(key) -> \(art.name) has no imageset directory")
                XCTAssertTrue(FileManager.default.fileExists(atPath: png.path),
                              "\(output.id) -> \(key) -> \(art.name) has no PNG")
                checked += 1
            }
        }
        XCTAssertGreaterThan(checked, 0, "no output ever resolved an asset key, test would pass vacuously")
    }

    // All eight d-pad tokens, pinned to their direction. Complements the
    // diagonal and rotation tests below, which cover the asset side.
    func testDPadTokensResolveToMatchingDirection() {
        let table: [(token: String, direction: OutputDirection)] = [
            ("dpad_N", .n), ("dpad_NE", .ne), ("dpad_E", .e), ("dpad_SE", .se),
            ("dpad_S", .s), ("dpad_SW", .sw), ("dpad_W", .w), ("dpad_NW", .nw),
        ]
        for row in table {
            let visual = OutputVisual.for(token: row.token)
            XCTAssertEqual(visual.kind, .dPad, row.token)
            XCTAssertEqual(visual.direction, row.direction, row.token)
        }
    }

    func testDiagonalFallsBackToTheNeutralPad() {
        for style in styles {
            let visual = OutputVisual.for(token: "dpad_NE", promptStyle: style)
            XCTAssertEqual(visual.assetKey, style == .xbox ? "xbox:dpad" : "ps:dpad")
            XCTAssertNotNil(OutputVisual.assetPath(for: visual.assetKey!), "diagonal key has no file")
        }
    }

    func testXboxDPadSouthIsNorthTurnedHalfway() {
        let north = OutputVisual.for(token: "dpad_N", promptStyle: .xbox)
        let south = OutputVisual.for(token: "dpad_S", promptStyle: .xbox)
        let northArt = OutputVisual.assetPath(for: north.assetKey!)!
        let southArt = OutputVisual.assetPath(for: south.assetKey!)!

        XCTAssertEqual(northArt.name, southArt.name)
        XCTAssertEqual(northArt.rotation, 0)
        XCTAssertEqual(southArt.rotation, 180)
    }

    // Pins the actual table, not just "four different glyphs": distinct is
    // not correct, and a swapped Cross/Square would still pass a distinctness
    // check while showing the wrong prompt on somebody's fire button. Values
    // read off FaceButtonAssetKey in the C#.
    func testFaceButtonsResolveToExpectedGlyphUnderEachStyle() {
        let table: [(token: String, face: ControllerFaceButton, ps: String, xbox: String)] = [
            ("x", .x, "ps:cross", "xbox:a"),
            ("circle", .circle, "ps:circle", "xbox:b"),
            ("square", .square, "ps:square", "xbox:x"),
            ("triangle", .triangle, "ps:triangle", "xbox:y"),
            ("A", .a, "ps:cross", "xbox:a"),
            ("B", .b, "ps:circle", "xbox:b"),
            ("X", .x, "ps:square", "xbox:x"),
            ("Y", .y, "ps:triangle", "xbox:y"),
        ]
        for row in table {
            let ps = OutputVisual.for(token: row.token, promptStyle: .playstation)
            let xbox = OutputVisual.for(token: row.token, promptStyle: .xbox)
            XCTAssertEqual(ps.kind, .faceButton, row.token)
            XCTAssertEqual(ps.faceButton, row.face, row.token)
            XCTAssertEqual(xbox.faceButton, row.face, row.token)
            XCTAssertEqual(ps.assetKey, row.ps, row.token)
            XCTAssertEqual(xbox.assetKey, row.xbox, row.token)
        }
    }

    // The desktop bug this guards against: matching a translated label's
    // first letter put the same glyph under every face button, because the
    // word for "B" does not start with B in every language. This resolver
    // never reads OutputAction.name at all, only the firmware keyword, so
    // an odd-language name must change nothing.
    func testFaceButtonArtComesOffTheTokenNotTheName() {
        let plain = QuadStickCatalog.output("controller-b")!
        let translated = OutputAction(id: "controller-b", name: "\u{4E38}ボタン", category: .controller)
        for style in styles {
            XCTAssertEqual(OutputVisual.for(plain, promptStyle: style).assetKey,
                           OutputVisual.for(translated, promptStyle: style).assetKey)
        }
    }

    // The full recognized mouse vocabulary. Kind is asserted first: if a
    // token fell through to .generic instead, checking assetKey alone would
    // still pass.
    func testMouseTokensResolveToMouseKindWithExpectedTextLabel() {
        let table: [(token: String, requiresTextLabel: Bool)] = [
            ("mouse_left_button", false), ("mouse_right_button", false),
            ("mouse_middle_button", false), ("mouse_wheel_up", false),
            ("mouse_wheel_down", false), ("mouse_back", false), ("mouse_forward", false),
            ("mouse_left", true), ("mouse_right", true), ("mouse_up", true),
            ("mouse_down", true), ("mouse_pan_left", true), ("mouse_pan_right", true),
        ]
        for row in table {
            let visual = OutputVisual.for(token: row.token)
            XCTAssertEqual(visual.kind, .mouse, row.token)
            XCTAssertEqual(visual.requiresTextLabel, row.requiresTextLabel, row.token)
            // ponytail cut: no mouse PNG set, see OutputVisual.swift.
            XCTAssertNil(visual.assetKey, row.token)
        }
    }

    func testKbAYieldsKeycapTextAndNoAssetKey() {
        let visual = OutputVisual.for(token: "kb_a")
        XCTAssertEqual(visual.kind, .keycap)
        XCTAssertEqual(visual.keycapText, "A")
        XCTAssertNil(visual.assetKey)
    }

    func testSpecialKeysGetSensibleKeycapText() {
        XCTAssertEqual(OutputVisual.for(token: "kb_grave_accent_and_tilde").keycapText, "Grave Accent And Tilde")
        XCTAssertEqual(OutputVisual.for(token: "kb_left_shift").keycapText, "Left Shift")
    }

    // Side, the four directions, click status, and that a click carries the
    // press-prompt asset. left_3/right_3 are the PS3 spelling for the same
    // click as left_stick/right_stick.
    func testStickTokensResolveSideDirectionAndClickStatus() {
        let table: [(token: String, side: ControllerStickSide, direction: OutputDirection?, isClick: Bool)] = [
            ("left_joy_up", .left, .n, false),
            ("left_joy_down", .left, .s, false),
            ("left_joy_left", .left, .w, false),
            ("left_joy_right", .left, .e, false),
            ("right_joy_up", .right, .n, false),
            ("right_joy_down", .right, .s, false),
            ("right_joy_left", .right, .w, false),
            ("right_joy_right", .right, .e, false),
            ("left_stick", .left, nil, true),
            ("right_stick", .right, nil, true),
            ("left_3", .left, nil, true),
            ("right_3", .right, nil, true),
        ]
        for row in table {
            let visual = OutputVisual.for(token: row.token)
            XCTAssertEqual(visual.kind, .joystick, row.token)
            XCTAssertEqual(visual.stickSide, row.side, row.token)
            XCTAssertEqual(visual.direction, row.direction, row.token)
            XCTAssertEqual(visual.isStickClick, row.isClick, row.token)
            XCTAssertEqual(visual.assetKey != nil, row.isClick, "\(row.token) asset key presence")
        }
    }

    // isTrigger and the hardware marking, PS3 spelling against Xbox spelling
    // for the same four physical buttons.
    func testShoulderTokensResolveIsTriggerAndMarking() {
        let table: [(token: String, isTrigger: Bool, ps: String, xbox: String)] = [
            ("left_1", false, "L1", "LB"),
            ("right_1", false, "R1", "RB"),
            ("left_2", true, "L2", "LT"),
            ("right_2", true, "R2", "RT"),
            ("left_bumper", false, "L1", "LB"),
            ("right_bumper", false, "R1", "RB"),
            ("left_trigger", true, "L2", "LT"),
            ("right_trigger", true, "R2", "RT"),
        ]
        for row in table {
            let ps = OutputVisual.for(token: row.token, promptStyle: .playstation)
            let xbox = OutputVisual.for(token: row.token, promptStyle: .xbox)
            XCTAssertEqual(ps.kind, .shoulder, row.token)
            XCTAssertEqual(ps.isTrigger, row.isTrigger, row.token)
            XCTAssertEqual(xbox.isTrigger, row.isTrigger, row.token)
            XCTAssertEqual(ps.symbol, row.ps, row.token)
            XCTAssertEqual(xbox.symbol, row.xbox, row.token)
        }
    }

    func testUnknownTokenIsFallbackAndNeverCrashes() {
        let visual = OutputVisual.for(token: "vendor_custom_action")
        XCTAssertTrue(visual.isFallback)
        XCTAssertEqual(visual.kind, .generic)
        XCTAssertEqual(visual.token, "vendor_custom_action")
        XCTAssertEqual(visual.friendlyLabel, "Vendor custom action")
    }

    // A profile read off a device or a Google Sheet is CSV: a token can
    // arrive as "dpad_N\r\n" or "\tdpad_N". .whitespaces alone does not
    // strip the newline, so this used to fall to the generic "?" fallback.
    func testTrailingCarriageReturnAndLeadingTabAreTrimmed() {
        let visual = OutputVisual.for(token: "\tdpad_N\r\n")
        XCTAssertEqual(visual.kind, .dPad)
        XCTAssertEqual(visual.direction, .n)
        XCTAssertFalse(visual.isFallback)
    }

    // "Never rewrite a value the user did not type," as a test: sweep every
    // known output under both styles and prove the token that comes back is
    // byte-identical to the firmware keyword that went in.
    func testTokenIsByteIdenticalAcrossTheWholeCatalog() {
        for style in styles {
            for output in QuadStickCatalog.outputs {
                let keyword = Firmware.keyword(forOutput: output.id) ?? output.id
                let visual = OutputVisual.for(output, promptStyle: style)
                XCTAssertEqual(visual.token, keyword, output.id)
            }
        }
    }

    func testControllerTypeMapsToPromptStyle() {
        XCTAssertEqual(ControllerType.xbox.promptStyle, .xbox)
        XCTAssertEqual(ControllerType.standard.promptStyle, .playstation)
        XCTAssertEqual(ControllerType.playstation.promptStyle, .playstation)
        // No Switch art set in this repo. Known gap, see the comment on
        // ControllerType.promptStyle.
        XCTAssertEqual(ControllerType.nintendoSwitch.promptStyle, .playstation)
    }
}
