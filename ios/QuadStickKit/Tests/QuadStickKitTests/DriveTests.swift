import XCTest
@testable import QuadStickKit

/// The Drive path end to end over a fake transport: sign in, push, conflict,
/// share, read back. No socket, no Google, no Keychain.
private final class FakeHTTP: HTTPFetching, @unchecked Sendable {
    typealias Responder = @Sendable (URLRequest) -> (Int, Data)
    private let responder: Responder
    private let lock = NSLock()
    private(set) var requests: [URLRequest] = []

    init(_ responder: @escaping Responder) { self.responder = responder }

    func data(for request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        lock.withLock { requests.append(request) }
        let (status, body) = responder(request)
        return (body, HTTPURLResponse(url: request.url!, statusCode: status,
                                      httpVersion: nil, headerFields: nil)!)
    }

    var urls: [String] { lock.withLock { requests.map { $0.url!.absoluteString } } }

    func body(matching fragment: String) -> [String: Any]? {
        lock.withLock {
            guard let r = requests.last(where: { $0.url!.absoluteString.contains(fragment) }),
                  let data = r.httpBody else { return nil }
            return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        }
    }
}

private func json(_ object: Any) -> Data {
    try! JSONSerialization.data(withJSONObject: object)
}

// MARK: - PKCE and the sign-in URL

final class GoogleAuthTests: XCTestCase {
    private let clientID = "123-abc.apps.googleusercontent.com"

    private func auth(_ http: FakeHTTP, store: TokenStoring = InMemoryTokenStore(),
                      now: @escaping @Sendable () -> Date = { Date(timeIntervalSince1970: 1_000) }) -> GoogleAuth {
        GoogleAuth(clientID: clientID, redirectURI: "com.googleusercontent.apps.123-abc:/oauth2redirect",
                   http: http, store: store, now: now)
    }

    // RFC 7636: base64url of SHA-256 of the verifier, no padding, no + or /.
    func testChallengeIsBase64URLOfTheHash() {
        // The RFC's own worked example.
        let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        XCTAssertEqual(GoogleAuth.challenge(for: verifier),
                       "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    func testVerifierIsFreshAndURLSafe() {
        let a = GoogleAuth.makeVerifier()
        let b = GoogleAuth.makeVerifier()
        XCTAssertNotEqual(a, b)
        XCTAssertEqual(a.count, 43)
        XCTAssertFalse(a.contains(where: { "+/=".contains($0) }))
    }

    // Without access_type=offline and prompt=consent Google sends no refresh
    // token, and Drive dies silently an hour after sign-in.
    func testAuthorizationURLAsksForOfflineAccess() throws {
        let url = auth(FakeHTTP { _ in (200, Data()) })
            .authorizationURL(challenge: "chal", state: "st")
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!
        func value(_ n: String) -> String? { items.first { $0.name == n }?.value }
        XCTAssertEqual(value("access_type"), "offline")
        XCTAssertEqual(value("prompt"), "consent")
        XCTAssertEqual(value("code_challenge_method"), "S256")
        XCTAssertEqual(value("scope"), "https://www.googleapis.com/auth/drive.file")
        XCTAssertEqual(value("client_id"), clientID)
    }

    // A callback whose state is not the one we sent is not our callback.
    func testCallbackWithTheWrongStateIsRejected() {
        let a = auth(FakeHTTP { _ in (200, Data()) })
        let url = URL(string: "com.googleusercontent.apps.123-abc:/oauth2redirect?code=c&state=other")!
        XCTAssertThrowsError(try a.code(from: url, expectedState: "mine")) { error in
            XCTAssertEqual(error as? GoogleAuthError, .stateMismatch)
        }
    }

    func testCallbackCarryingAnErrorIsRejected() {
        let a = auth(FakeHTTP { _ in (200, Data()) })
        let url = URL(string: "x:/cb?error=access_denied&state=mine")!
        XCTAssertThrowsError(try a.code(from: url, expectedState: "mine"))
    }

    func testCallbackWithTheRightStateGivesTheCode() throws {
        let a = auth(FakeHTTP { _ in (200, Data()) })
        let url = URL(string: "x:/cb?code=abc123&state=mine")!
        XCTAssertEqual(try a.code(from: url, expectedState: "mine"), "abc123")
    }

    func testExchangeStoresTheRefreshToken() async throws {
        let http = FakeHTTP { _ in (200, json(["access_token": "at", "expires_in": 3600, "refresh_token": "rt"])) }
        let store = InMemoryTokenStore()
        try await auth(http, store: store).exchange(code: "c", verifier: "v")
        XCTAssertEqual(store.load(), "rt")
    }

    // A re-consent that returns no refresh token must not wipe the one we have,
    // which would sign the user out for no reason.
    func testExchangeWithoutARefreshTokenKeepsTheStoredOne() async throws {
        let http = FakeHTTP { _ in (200, json(["access_token": "at", "expires_in": 3600])) }
        let store = InMemoryTokenStore(token: "old")
        try await auth(http, store: store).exchange(code: "c", verifier: "v")
        XCTAssertEqual(store.load(), "old")
    }

    // The user revoked access in their Google account. Retrying is pointless,
    // so this has to arrive as its own case and not as a generic failure.
    func testRevokedRefreshTokenIsItsOwnError() async {
        let http = FakeHTTP { _ in (400, json(["error": "invalid_grant"])) }
        let a = auth(http, store: InMemoryTokenStore(token: "rt"))
        do {
            _ = try await a.accessToken()
            XCTFail("expected revoked")
        } catch {
            XCTAssertEqual(error as? GoogleAuthError, .revoked)
        }
    }

    func testNoStoredTokenReadsAsNotConnected() async {
        let a = auth(FakeHTTP { _ in (200, Data()) }, store: InMemoryTokenStore())
        do {
            _ = try await a.accessToken()
            XCTFail("expected notConnected")
        } catch {
            XCTAssertEqual(error as? GoogleAuthError, .notConnected)
        }
    }

    // A cached token is reused, and a stale one is refreshed. The minute of
    // slack keeps a request from starting on a token that dies mid-flight.
    func testAccessTokenIsCachedThenRefreshedWhenStale() async throws {
        let http = FakeHTTP { _ in (200, json(["access_token": "at", "expires_in": 120])) }
        nonisolated(unsafe) var clock = Date(timeIntervalSince1970: 1_000)
        let a = GoogleAuth(clientID: clientID, redirectURI: "x:/cb", http: http,
                           store: InMemoryTokenStore(token: "rt"), now: { clock })
        _ = try await a.accessToken()
        _ = try await a.accessToken()
        XCTAssertEqual(http.urls.count, 1, "second call should use the cache")
        clock = clock.addingTimeInterval(90)   // inside the 60s slack
        _ = try await a.accessToken()
        XCTAssertEqual(http.urls.count, 2, "a stale token should be refreshed")
    }

    func testAPlaceholderClientIdIsNotConfigured() {
        XCTAssertFalse(GoogleClient.isConfigured(""))
        XCTAssertFalse(GoogleClient.isConfigured("REPLACE-ME"))
        XCTAssertTrue(GoogleClient.isConfigured(clientID))
        XCTAssertEqual(GoogleClient.reversedScheme(clientID), "com.googleusercontent.apps.123-abc")
    }
}

// MARK: - Talking to Sheets and Drive

final class DriveClientTests: XCTestCase {
    private func client(_ http: FakeHTTP) -> DriveClient {
        DriveClient(http: http, token: { "tok" })
    }

    private let oneTab = json(["sheets": [["properties": ["sheetId": 0, "title": "Driving"]]]])

    func testEveryRequestCarriesTheBearerToken() async throws {
        let http = FakeHTTP { _ in (200, json(["modifiedTime": "t1"])) }
        _ = try await client(http).modifiedTime("sheet-1")
        XCTAssertEqual(http.requests.first?.value(forHTTPHeaderField: "Authorization"), "Bearer tok")
    }

    // A blank profile would clear every range and leave the sheet empty. The
    // sheet is often the only copy that is not on this phone.
    func testAnEmptyProfileIsNeverPushed() async throws {
        let http = FakeHTTP { _ in (200, Data()) }
        try await client(http).push([SheetTab(title: "Empty", rows: [["", "  "]])], to: "sheet-1")
        XCTAssertTrue(http.urls.isEmpty)
    }

    // Values are written before the leftovers are cleared. The other order
    // leaves the sheet blank if the write fails.
    func testPushWritesBeforeItClears() async throws {
        let http = FakeHTTP { r in
            (200, r.url!.absoluteString.contains("?fields=") ? self.oneTab : Data())
        }
        try await client(http).push([SheetTab(title: "Driving", rows: [["mouse_left", "normal", "lip"]])], to: "s")
        let update = http.urls.firstIndex { $0.contains("values:batchUpdate") }
        let clear = http.urls.firstIndex { $0.contains("values:batchClear") }
        XCTAssertNotNil(update)
        XCTAssertNotNil(clear)
        XCTAssertLessThan(update!, clear!)
    }

    // A pasted "=SUM(...)" is a cell of somebody's profile, not a formula to run.
    func testValuesAreWrittenRaw() async throws {
        let http = FakeHTTP { r in
            (200, r.url!.absoluteString.contains("?fields=") ? self.oneTab : Data())
        }
        try await client(http).push([SheetTab(title: "Driving", rows: [["=1+1"]])], to: "s")
        XCTAssertEqual(http.body(matching: "values:batchUpdate")?["valueInputOption"] as? String, "RAW")
    }

    // Rows are padded to one width, so an input a binding lost is blanked by
    // the write rather than left showing its old value.
    func testShortRowsArePaddedToTheWidestRow() async throws {
        let http = FakeHTTP { r in
            (200, r.url!.absoluteString.contains("?fields=") ? self.oneTab : Data())
        }
        try await client(http).push([SheetTab(title: "Driving", rows: [["a", "b", "c"], ["d"]])], to: "s")
        let data = http.body(matching: "values:batchUpdate")?["data"] as? [[String: Any]]
        let values = data?.first?["values"] as? [[String]]
        XCTAssertEqual(values?[1], ["d", "", ""])
    }

    func testReadingBackRebuildsTheCsvWithABlankLineBetweenTabs() async throws {
        let http = FakeHTTP { r in
            let url = r.url!.absoluteString
            if url.contains("?fields=") {
                return (200, json(["sheets": [["properties": ["sheetId": 0, "title": "A"]],
                                              ["properties": ["sheetId": 1, "title": "B"]]]]))
            }
            return (200, json(["valueRanges": [
                ["values": [["Profile Name", "", "A"], ["a.csv"]]],
                ["values": [["Profile Name", "", "B"], ["b.csv"]]],
            ]]))
        }
        let csv = try await client(http).downloadProfileCSV("s")
        XCTAssertEqual(csv, "Profile Name,,A\r\na.csv\r\n\r\nProfile Name,,B\r\nb.csv\r\n")
    }

    // The device ends a mode at an empty line, so a round trip that loses the
    // blank row folds the second mode into the first.
    func testAReadBackFileStillImportsAsTwoModes() async throws {
        let http = FakeHTTP { r in
            let url = r.url!.absoluteString
            if url.contains("?fields=") {
                return (200, json(["sheets": [["properties": ["sheetId": 0, "title": "A"]],
                                              ["properties": ["sheetId": 1, "title": "B"]]]]))
            }
            return (200, json(["valueRanges": [
                ["values": [["QuadStick Configuration", "Version 1.5", "", "Game"], [],
                            ["Profile Name", "", "A"], ["game.csv"], ["Outputs", "Function", "usb"],
                            ["mouse_left", "normal", "lip"]]],
                ["values": [["Profile Name", "", "B"], ["game.csv"], ["Outputs", "Function", "usb"],
                            ["mouse_right", "normal", "hard_puff"]]],
            ]]))
        }
        let csv = try await client(http).downloadProfileCSV("s")
        let result = try XCTUnwrap(DeviceFile.importProfile(csv: csv, fallbackName: "x"))
        XCTAssertEqual(result.profile.modes.count, 2)
        XCTAssertEqual(result.profile.modes.map(\.name), ["A", "B"])
    }

    func testA404ReadsAsNotFoundSoTheCallerCanOfferANewSheet() async {
        let http = FakeHTTP { _ in (404, Data("gone".utf8)) }
        do {
            _ = try await client(http).modifiedTime("sheet-1")
            XCTFail("expected notFound")
        } catch {
            XCTAssertEqual(error as? DriveError, .notFound)
        }
    }

    func testA403ReadsAsNotAuthorized() async {
        let http = FakeHTTP { _ in (403, Data()) }
        do {
            _ = try await client(http).listSpreadsheets()
            XCTFail("expected notAuthorized")
        } catch {
            XCTAssertEqual(error as? DriveError, .notAuthorized)
        }
    }

    // Link-only, not searchable, read-only.
    func testSharingIsAnyoneWithTheLinkAndReadOnly() async throws {
        let http = FakeHTTP { _ in (200, Data()) }
        try await client(http).shareAnyoneReader("s")
        let body = http.body(matching: "/permissions")
        XCTAssertEqual(body?["role"] as? String, "reader")
        XCTAssertEqual(body?["type"] as? String, "anyone")
        XCTAssertEqual(body?["allowFileDiscovery"] as? Bool, false)
    }

    func testListingFollowsEveryPage() async throws {
        nonisolated(unsafe) var call = 0
        let http = FakeHTTP { _ in
            call += 1
            return call == 1
                ? (200, json(["files": [["id": "a", "name": "A", "modifiedTime": "t"]], "nextPageToken": "p2"]))
                : (200, json(["files": [["id": "b", "name": "B", "modifiedTime": "t"]]]))
        }
        let sheets = try await client(http).listSpreadsheets()
        XCTAssertEqual(sheets.map(\.id), ["a", "b"])
    }

    func testTabNamesAreQuotedForA1AndApostrophesDoubled() {
        XCTAssertEqual(DriveClient.quoted("Bob's mode"), "'Bob''s mode'")
        XCTAssertEqual(DriveClient.columnName(1), "A")
        XCTAssertEqual(DriveClient.columnName(26), "Z")
        XCTAssertEqual(DriveClient.columnName(27), "AA")
    }
}

// MARK: - Push, conflict, share

final class DriveSyncTests: XCTestCase {
    private let csv = "QuadStick Configuration,Version 1.5,,Game\r\n\r\nProfile Name,,Driving\r\ngame.csv\r\nOutputs,Function,usb\r\nmouse_left,normal,lip\r\n"

    private func sync(_ http: FakeHTTP) -> DriveSync {
        DriveSync(client: DriveClient(http: http, token: { "tok" }))
    }

    private func responder(modified: @escaping @Sendable () -> String) -> FakeHTTP {
        FakeHTTP { r in
            let url = r.url!.absoluteString
            if url.contains("fields=modifiedTime") { return (200, json(["modifiedTime": modified()])) }
            if url.contains("?fields=") { return (200, json(["sheets": [["properties": ["sheetId": 0, "title": "Driving"]]]])) }
            if url.hasSuffix("/v4/spreadsheets") { return (200, json(["spreadsheetId": "new-sheet"])) }
            return (200, Data())
        }
    }

    func testFirstPushCreatesTheSheet() async throws {
        let http = responder(modified: { "t1" })
        let result = try await sync(http).push(profile: Profile(name: "Game", modes: [Mode(name: "Driving")]),
                                               csv: csv, resolve: { .replaceWithMine })
        XCTAssertEqual(result, .pushed(sheetID: "new-sheet", modifiedTime: "t1"))
    }

    // Nothing changed online, so no question is asked.
    func testAnUntouchedSheetIsPushedWithoutAsking()  async throws {
        let http = responder(modified: { "t1" })
        nonisolated(unsafe) var asked = false
        let profile = Profile(name: "Game", modes: [Mode(name: "Driving")],
                              sheetID: "s1", sheetSyncedTime: "t1")
        let result = try await sync(http).push(profile: profile, csv: csv,
                                               resolve: { asked = true; return .replaceWithMine })
        XCTAssertFalse(asked)
        XCTAssertEqual(result, .pushed(sheetID: "s1", modifiedTime: "t1"))
    }

    // Somebody edited the sheet on a laptop. A phone that has been asleep must
    // not overwrite that without a word.
    func testAnEditedSheetAsksBeforeOverwriting() async throws {
        let http = responder(modified: { "t2" })
        nonisolated(unsafe) var asked = false
        let profile = Profile(name: "Game", modes: [Mode(name: "Driving")],
                              sheetID: "s1", sheetSyncedTime: "t1")
        _ = try await sync(http).push(profile: profile, csv: csv,
                                      resolve: { asked = true; return .replaceWithMine })
        XCTAssertTrue(asked)
    }

    func testKeepingTheOnlineVersionDownloadsItAndWritesNothing() async throws {
        let http = FakeHTTP { r in
            let url = r.url!.absoluteString
            if url.contains("fields=modifiedTime") { return (200, json(["modifiedTime": "t2"])) }
            if url.contains("?fields=") { return (200, json(["sheets": [["properties": ["sheetId": 0, "title": "Driving"]]]])) }
            if url.contains("values:batchGet") {
                return (200, json(["valueRanges": [["values": [["Profile Name", "", "Online"]]]]]))
            }
            return (200, Data())
        }
        let profile = Profile(name: "Game", modes: [Mode(name: "Driving")],
                              sheetID: "s1", sheetSyncedTime: "t1")
        let result = try await sync(http).push(profile: profile, csv: csv, resolve: { .keepOnline })
        guard case .keptOnline(let id, let text) = result else { return XCTFail("expected keptOnline") }
        XCTAssertEqual(id, "s1")
        XCTAssertTrue(text.contains("Online"))
        XCTAssertFalse(http.urls.contains { $0.contains("values:batchUpdate") },
                       "keeping the online version must not write to it")
    }

    // The link must never point at a sheet a version behind what the sender is
    // looking at, so sharing pushes first.
    func testSharePushesBeforeItHandsOutTheLink() async throws {
        let http = responder(modified: { "t1" })
        let (url, _) = try await sync(http).shareLink(
            profile: Profile(name: "Game", modes: [Mode(name: "Driving")]),
            csv: csv, resolve: { .replaceWithMine })
        XCTAssertEqual(url.absoluteString, "https://docs.google.com/spreadsheets/d/new-sheet/edit?usp=sharing")
        let update = http.urls.firstIndex { $0.contains("values:batchUpdate") }
        let permissions = http.urls.firstIndex { $0.contains("/permissions") }
        XCTAssertNotNil(update)
        XCTAssertLessThan(update!, permissions!)
    }

    func testRecreateMakesANewSheetForADeletedOne() async throws {
        let http = responder(modified: { "t9" })
        let profile = Profile(name: "Game", modes: [Mode(name: "Driving")],
                              sheetID: "deleted", sheetSyncedTime: "t1")
        let result = try await sync(http).recreate(profile: profile, csv: csv)
        XCTAssertEqual(result, .pushed(sheetID: "new-sheet", modifiedTime: "t9"))
    }

    func testMineIsNewestFirst() async throws {
        let http = FakeHTTP { _ in
            (200, json(["files": [["id": "old", "name": "Old", "modifiedTime": "2026-01-01T00:00:00Z"],
                                  ["id": "new", "name": "New", "modifiedTime": "2026-08-01T00:00:00Z"]]]))
        }
        let ids = try await sync(http).mine().map(\.id)
        XCTAssertEqual(ids, ["new", "old"])
    }
}

// MARK: - The shape a sheet is written in

final class SheetTabsTests: XCTestCase {
    func testOneTabPerModeTitledWithTheModeName() {
        let csv = DeviceFile.export(Profile(name: "Game", modes: [Mode(name: "Driving"), Mode(name: "Menus")]))
        XCTAssertEqual(SheetTabs.split(csv: csv).map(\.title), ["Driving", "Menus"])
    }

    // The version header sits above the first keyword row, so it has to travel
    // on the first tab or the sheet id and the profile name are dropped.
    func testTheHeaderTravelsOnTheFirstTab() {
        let csv = DeviceFile.export(Profile(name: "Game", modes: [Mode(name: "Driving")], sheetID: "abc"))
        let first = SheetTabs.split(csv: csv).first
        XCTAssertEqual(first?.rows.first?.first, "QuadStick Configuration")
        XCTAssertEqual(first?.rows.first?[2], "abc")
    }

    // Two modes may share a name because the device tells them apart by
    // position. Two tabs in one spreadsheet may not.
    func testTwoModesWithOneNameGetDistinctTabs() {
        let csv = DeviceFile.export(Profile(name: "Game", modes: [Mode(name: "Same"), Mode(name: "Same")]))
        XCTAssertEqual(SheetTabs.split(csv: csv).map(\.title), ["Same", "Same (2)"])
    }

    func testCharactersSheetsRefusesAreStrippedFromATabName() {
        XCTAssertEqual(SheetTabs.safe("Drive/Fly[1]"), "DriveFly1")
        XCTAssertEqual(SheetTabs.safe("   "), "Mode")
    }

    func testATitlePast100CharactersIsCut() {
        XCTAssertEqual(SheetTabs.safe(String(repeating: "a", count: 150)).count, 100)
    }

    func testSomethingWithNoKeywordAtAllIsPushedWhole() {
        let tabs = SheetTabs.split(csv: "just,some,rows\r\nand,more,rows\r\n")
        XCTAssertEqual(tabs.count, 1)
        XCTAssertEqual(tabs.first?.title, "Profile")
        XCTAssertEqual(tabs.first?.rows.count, 2)
    }

    // A tab keeps no trailing blank rows: the blank line between modes belongs
    // to the file, and a reader puts one back between tabs.
    func testTrailingBlankRowsAreNotPartOfATab() {
        let csv = DeviceFile.export(Profile(name: "Game", modes: [Mode(name: "A"), Mode(name: "B")]))
        for tab in SheetTabs.split(csv: csv) {
            XCTAssertFalse(tab.rows.last?.allSatisfy { $0.isEmpty } ?? false)
        }
    }
}
