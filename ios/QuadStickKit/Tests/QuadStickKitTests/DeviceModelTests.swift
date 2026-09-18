import XCTest
@testable import QuadStickKit

/// There are three QuadStick models now, not one. Each has its own photo,
/// its own measured hotspots, and its own set of inputs, and a mismatch
/// between any two of those is what used to show an Original owner an FPS
/// photo, or a Singleton owner three holes their device has not got.
final class DeviceModelTests: XCTestCase {

    // MARK: - The photo on disk is the one the numbers were measured on

    // ponytail: the photos live in the app target's asset catalog, not this
    // package, so this walks the file tree to read them directly instead of
    // bundling a second copy just for the test.
    private static func assetPNGURL(_ name: String) -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // QuadStickKitTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // QuadStickKit
            .deletingLastPathComponent()   // ios
            .appendingPathComponent("App/Assets.xcassets/\(name).imageset/\(name).png")
    }

    // Read the PNG header rather than decoding: bytes 16..20 are the width,
    // 20..24 the height, big endian, straight after the IHDR chunk tag.
    private static func pngSize(_ url: URL) throws -> (width: Int, height: Int) {
        let data = try Data(contentsOf: url)
        let head = [UInt8](data.prefix(24))
        XCTAssertEqual(head.count, 24, "\(url.lastPathComponent) is not a readable PNG")
        func word(_ offset: Int) -> Int {
            Int(head[offset]) << 24 | Int(head[offset + 1]) << 16
                | Int(head[offset + 2]) << 8 | Int(head[offset + 3])
        }
        return (word(16), word(20))
    }

    /// Every hotspot and mode-light number in DevicePhoto is measured off
    /// one file, as a fraction of that file. Dropping in a differently
    /// framed picture leaves the numbers pointing at the wrong holes, so
    /// this pins the file's own pixel size and fails loudly on a swap.
    func testEachPhotoIsTheOneItsHotspotsWereMeasuredOn() throws {
        for model in QuadStickModel.allCases {
            let photo = DevicePhoto.for(model)
            let size = try Self.pngSize(Self.assetPNGURL(photo.assetName))
            XCTAssertEqual(size.width, photo.nativeWidth, "\(photo.assetName) width")
            XCTAssertEqual(size.height, photo.nativeHeight, "\(photo.assetName) height")
        }
    }

    func testNoModelBorrowsAnotherModelsPicture() {
        let names = Set(QuadStickModel.allCases.map { DevicePhoto.for($0).assetName })
        XCTAssertEqual(names.count, QuadStickModel.allCases.count, "two models share one photo")
        XCTAssertEqual(names, ["QuadStickFPS", "QuadStickOriginal", "QuadStickSingleton"])
    }

    func testFPSAndOriginalHaveDistinctHotspotsAndLights() {
        let fps = DevicePhoto.for(.fps)
        let original = DevicePhoto.for(.original)
        XCTAssertNotEqual(fps.hotspots.map(\.x), original.hotspots.map(\.x))
        XCTAssertNotEqual(fps.hotspots.map(\.y), original.hotspots.map(\.y))
        XCTAssertNotEqual(fps.lights, original.lights)
    }

    // The Singleton is photographed at an angle, so its five lenses are not
    // evenly spaced in the file. A start-and-gap pair would put the outer
    // two off the glass, so this row carries the five measured centers.
    func testSingletonsLightsComeFromMeasuredCentresNotEvenSpacing() {
        let row = DevicePhoto.for(.singleton).lights
        let measured = [690.0, 815.0, 935.0, 1048.0, 1159.0]
        for (i, px) in measured.enumerated() {
            XCTAssertEqual(row.xAt(i), px / 2048, accuracy: 0.000001)
        }
        let gaps = (0..<4).map { row.xAt($0 + 1) - row.xAt($0) }
        XCTAssertTrue(gaps.allSatisfy { $0 > 0 }, "the lights read left to right")
        XCTAssertGreaterThan(gaps[0] - gaps[3], 0.005,
                             "evenly spaced lights would mean the measurement was thrown away")
    }

    // MARK: - Every callout names a part the model has

    func testEveryHotspotsInputExistsInThatModelsCapabilities() {
        for model in QuadStickModel.allCases {
            let caps = QuadStickCatalog.capabilities(for: model)
            for spot in DevicePhoto.for(model).hotspots {
                XCTAssertNotNil(caps.input(spot.inputID),
                                "\(model) has a hotspot for \(spot.inputID), which is not one of its inputs")
            }
        }
    }

    // A point outside the crop is drawn off the photo entirely, past its
    // edge. Arithmetic alone will not catch that; the crop itself has to
    // hold it. This matters most for the Singleton, whose crop is a small
    // rectangle inside a much larger file.
    func testEveryHotspotLiesInsideTheDrawnCrop() {
        for model in QuadStickModel.allCases {
            let photo = DevicePhoto.for(model)
            for spot in photo.hotspots {
                let p = photo.position(of: spot)
                XCTAssertTrue((0...1).contains(p.x), "\(model): \(spot.inputID) x \(p.x) is off the crop")
                XCTAssertTrue((0...1).contains(p.y), "\(model): \(spot.inputID) y \(p.y) is off the crop")
            }
        }
    }

    // The reverse of testEveryHotspotsInputExistsInThatModelsCapabilities:
    // every front part the model actually has needs a ring on its photo, or
    // that part is unreachable from the picture.
    func testEveryFrontInputHasARegionOnThePhoto() {
        let expected: [QuadStickModel: Int] = [.fps: 6, .original: 6, .singleton: 2]
        for model in QuadStickModel.allCases {
            let caps = QuadStickCatalog.capabilities(for: model)
            let front = Set(caps.inputs.filter { $0.face == .front }.map(\.id))
            let covered = Set(DevicePhoto.for(model).hotspots.map(\.inputID))
            XCTAssertEqual(front, covered, "\(model): front inputs and photo hotspots do not match")
            XCTAssertEqual(front.count, expected[model], "\(model): expected front input count")
        }
    }

    // MARK: - No two hotspots on a photo overlap

    // DevicePhotoView sizes the tappable region to
    // size.width * minimumSeparation (DevicePhotoView.swift). HomeView pins
    // the photo's column to at most 640pt (HomeView.swift,
    // .frame(maxWidth: 640)), so 640pt is the widest a plausible photo gets
    // drawn. A 44pt target there needs minimumSeparation >= 44 / 640, i.e.
    // 0.06875. A narrower phone shrinks the ring by the same fraction, which
    // is an existing tradeoff outside this phase, not something this test
    // hides.
    func testNoTwoHotspotsOverlapAndTheGapFitsA44ptTarget() {
        let requiredSeparationFor44pt = 44.0 / 640.0
        for model in QuadStickModel.allCases {
            let photo = DevicePhoto.for(model)
            let points = photo.hotspots.map(photo.position)
            for i in points.indices {
                for j in points.indices.dropFirst(i + 1) {
                    let dx = points[i].x - points[j].x
                    let dy = (points[i].y - points[j].y) / photo.aspectRatio
                    let distance = (dx * dx + dy * dy).squareRoot()
                    XCTAssertGreaterThanOrEqual(distance, photo.minimumSeparation - 0.000001,
                        "\(model): \(photo.hotspots[i].inputID) and \(photo.hotspots[j].inputID) "
                      + "are closer than the model's own minimumSeparation")
                }
            }
            XCTAssertGreaterThan(photo.minimumSeparation, requiredSeparationFor44pt,
                "\(model): minimumSeparation \(photo.minimumSeparation) is too tight for a 44pt "
              + "target even at the widest the photo is drawn (640pt)")
        }
    }

    /// Every ring on a photo is labelled, and the labels are short enough not
    /// to run into their neighbour, and no two on the same photo read alike.
    func testEveryHotspotHasAShortUniqueLabel() {
        for model in QuadStickModel.allCases {
            let names = DevicePhoto.for(model).hotspots.map(\.shortName)
            XCTAssertTrue(names.allSatisfy { !$0.isEmpty && $0.count <= 8 },
                         "\(model): a label longer than 8 characters collides with its neighbour")
            XCTAssertEqual(Set(names).count, names.count, "\(model): two rings read the same")
        }
    }

    // The Singleton has one tube, so calling it "Center" implies a left and
    // a right that do not exist on this model.
    func testSingletonsTubeReadsTubeNotCenter() {
        let spot = DevicePhoto.for(.singleton).hotspots.first { $0.inputID == "center-tube" }
        XCTAssertEqual(spot?.shortName, "Tube")
    }

    // MARK: - Capabilities per model

    func testSingletonHasExactlyThreeInputsTheOtherTwoHaveTheFullSet() {
        let singleton = QuadStickCatalog.capabilities(for: .singleton)
        XCTAssertEqual(Set(singleton.inputs.map(\.id)), ["center-tube", "joystick", "usb-host"])

        let fps = QuadStickCatalog.capabilities(for: .fps)
        let original = QuadStickCatalog.capabilities(for: .original)
        XCTAssertEqual(fps.inputs.count, 10)
        XCTAssertEqual(Set(fps.inputs.map(\.id)), Set(original.inputs.map(\.id)))
    }

    func testCapabilitiesModelNameMatchesTheDisplayName() {
        for model in QuadStickModel.allCases {
            XCTAssertEqual(QuadStickCatalog.capabilities(for: model).model, model.displayName)
            XCTAssertEqual(QuadStickCatalog.capabilities(for: model).ledCount, 4)
        }
    }

    // MARK: - A profile that maps more than the chosen model has

    func testInputsNotOnFindsTheLeftTubeForASingletonOpeningFortnite() {
        let missing = QuadStickCatalog.inputsNotOn(.singleton, mappedBy: SampleData.fortnite)
        XCTAssertTrue(missing.contains { $0.id == "left-tube" })
    }

    func testInputsNotOnIsEmptyForAnFPSOpeningFortnite() {
        XCTAssertTrue(QuadStickCatalog.inputsNotOn(.fps, mappedBy: SampleData.fortnite).isEmpty)
    }

    func testInputsNotOnIsEmptyWhenNothingIsMapped() {
        let empty = Profile(name: "Blank", modes: [Mode(name: "One")])
        XCTAssertTrue(QuadStickCatalog.inputsNotOn(.singleton, mappedBy: empty).isEmpty)
    }

    // MARK: - Settings on a phone that predate this field

    func testGlobalSettingsDecodesWithoutADeviceModelKey() throws {
        let json = """
        {"joystickSensitivity":60,"sipPuffThreshold":35,"deadZone":8,
         "bootPS4":true,"titanTwoPS4":false,"usbHostMode":false}
        """
        let settings = try JSONDecoder().decode(GlobalSettings.self, from: Data(json.utf8))
        XCTAssertEqual(settings.deviceModel, .fps)
        XCTAssertEqual(settings.joystickSensitivity, 60)
        XCTAssertTrue(settings.bootPS4)
    }

    func testGlobalSettingsRoundTripsAChosenModel() throws {
        var settings = GlobalSettings()
        settings.deviceModel = .singleton
        let data = try JSONEncoder().encode(settings)
        let reloaded = try JSONDecoder().decode(GlobalSettings.self, from: data)
        XCTAssertEqual(reloaded.deviceModel, .singleton)
    }

    // MARK: - Picker order

    func testModelOrderIsFpsOriginalSingleton() {
        XCTAssertEqual(QuadStickModel.allCases, [.fps, .original, .singleton])
    }

    // MARK: - A part the chosen model lacks still opens and is still named

    // The screens that edit one mapping are reached by action id. Resolving
    // that id through the chosen model's capabilities hands back nothing for a
    // part that model does not have, and the row opens on a blank page. These
    // two pin the catalog-wide lookup that the UI and the validator use
    // instead.
    func testCatalogFindsAnActionTheChosenModelDoesNotHave() {
        let singleton = QuadStickCatalog.capabilities(for: .singleton)
        XCTAssertNil(singleton.action("left-tube-normal-sip"),
                     "the Singleton has no left tube, so its own capabilities must not claim one")
        XCTAssertEqual(QuadStickCatalog.action("left-tube-normal-sip")?.fullName,
                       "Left Tube Normal Sip")
    }

    func testAnIssueOnAnOffModelPartIsNamedInWords() {
        var profile = SampleData.fortnite
        profile.modes = [Mode(name: "Movement", assignments: [
            "left-tube-normal-sip": Assignment(output: QuadStickCatalog.output("controller-a"),
                                               function: .greaterThan(percent: 250)),
        ])]

        let issues = ConfigValidator.validate(profile,
                                              capabilities: QuadStickCatalog.capabilities(for: .singleton))
        let activation = issues.first { $0.id.hasPrefix("gt-") }
        XCTAssertNotNil(activation, "an impossible activation point is still an error off model")
        XCTAssertTrue(activation?.location.contains("Left Tube Normal Sip") ?? false,
                      "got \(activation?.location ?? "no issue"), which names the raw id instead of the part")
    }
}
