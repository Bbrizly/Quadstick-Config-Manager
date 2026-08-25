import SwiftUI
import AuthenticationServices
import QuadStickKit

/// Google Drive, from the app's side: signing in, pushing a profile to its
/// sheet, and handing back a link.
///
/// Sign-in runs in ASWebAuthenticationSession rather than a web view inside the
/// app. Google refuses embedded web views for OAuth, and the system sheet is
/// also the only one that can reach an existing Google session, so most people
/// tap once and are done.
@Observable
@MainActor
final class DriveAccount: NSObject, ASWebAuthenticationPresentationContextProviding {
    enum Status: Equatable {
        case notConfigured
        case signedOut
        case working(String)
        case signedIn
        case problem(String)
    }

    private(set) var status: Status
    private let auth: GoogleAuth
    private let sync: DriveSync
    private let clientID: String

    /// Set when a push found the sheet changed online. The view puts the choice
    /// to the user; nothing is written until it answers.
    var conflict: Conflict?

    /// True when the last failure was a sheet that is not in Drive any more.
    /// A flag, not a search of the message, so rewording the sentence people
    /// read cannot quietly disable the offer to make a new one.
    private(set) var lastFailureWasMissingSheet = false

    struct Conflict: Identifiable {
        let id = UUID()
        let profileName: String
        let decide: @Sendable (ConflictChoice) -> Void
    }

    override init() {
        let store = KeychainTokenStore()
        let clientID = GoogleClient.id
        let auth = GoogleAuth(clientID: clientID, store: store)
        self.clientID = clientID
        self.auth = auth
        self.sync = DriveSync(client: DriveClient(token: { try await auth.accessToken() }))
        // Reading the Keychain is the only way to know: a refresh token
        // outlives the app, so a fresh launch can already be signed in.
        self.status = GoogleClient.isConfigured(clientID)
            ? (store.load() == nil ? .signedOut : .signedIn)
            : .notConfigured
        super.init()
    }

    var isSignedIn: Bool { status == .signedIn }

    var isConfigured: Bool { status != .notConfigured }

    // MARK: - Signing in

    func signIn() async {
        guard isConfigured else { return }
        let verifier = GoogleAuth.makeVerifier()
        let state = GoogleAuth.makeVerifier()
        let url = auth.authorizationURL(challenge: GoogleAuth.challenge(for: verifier), state: state)
        guard let scheme = GoogleClient.reversedScheme(clientID) else { return }

        status = .working("Opening Google...")
        do {
            let callback = try await presentSignIn(url: url, scheme: scheme)
            let code = try await auth.code(from: callback, expectedState: state)
            status = .working("Finishing sign-in...")
            try await auth.exchange(code: code, verifier: verifier)
            status = .signedIn
        } catch is CancellationError {
            status = .signedOut
        } catch let error as ASWebAuthenticationSessionError where error.code == .canceledLogin {
            status = .signedOut
        } catch {
            status = .problem(Self.explain(error))
        }
    }

    private func presentSignIn(url: URL, scheme: String) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callbackURLScheme: scheme) { callback, error in
                if let callback { continuation.resume(returning: callback) }
                else { continuation.resume(throwing: error ?? URLError(.cancelled)) }
            }
            session.presentationContextProvider = self
            // The whole point is reaching a Google session the person is
            // already signed into, so this stays false.
            session.prefersEphemeralWebBrowserSession = false
            session.start()
        }
    }

    nonisolated func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            return scenes.flatMap(\.windows).first { $0.isKeyWindow } ?? ASPresentationAnchor()
        }
    }

    func signOut() async {
        await auth.signOut()
        status = .signedOut
    }

    // MARK: - Using it

    /// Push a profile and hand back what changed, for the caller to store on
    /// the profile. Returns nil when it did not happen, having already put the
    /// reason in `status`.
    func push(_ profile: Profile) async -> PushResult? {
        await run("Saving to Google Drive...") {
            try await self.sync.push(profile: profile,
                                     csv: DeviceFile.export(profile),
                                     resolve: self.askAboutConflict(profile.name))
        }
    }

    func shareLink(for profile: Profile) async -> (url: URL, result: PushResult)? {
        await run("Putting this profile in Google Sheets...") {
            try await self.sync.shareLink(profile: profile,
                                          csv: DeviceFile.export(profile),
                                          resolve: self.askAboutConflict(profile.name))
        }
    }

    func recreate(_ profile: Profile) async -> PushResult? {
        await run("Making a new sheet...") {
            try await self.sync.recreate(profile: profile, csv: DeviceFile.export(profile))
        }
    }

    func mySheets() async -> [DriveSheetInfo]? {
        await run("Looking for your sheets...") { try await self.sync.mine() }
    }

    func download(_ id: String) async -> String? {
        await run("Downloading...") { try await self.sync.download(id) }
    }

    /// One wrapper for every call: say what is happening, turn whatever comes
    /// back into words a person can act on, and never leave the status stuck on
    /// a message about work that has stopped.
    private func run<T>(_ message: String, _ work: @escaping () async throws -> T) async -> T? {
        guard isConfigured else { return nil }
        status = .working(message)
        lastFailureWasMissingSheet = false
        do {
            let value = try await work()
            status = .signedIn
            return value
        } catch {
            lastFailureWasMissingSheet = (error as? DriveError) == .notFound
            status = .problem(Self.explain(error))
            return nil
        }
    }

    /// Hands the decision to the view and waits for it. The sheet changed
    /// online, so somebody has to say whether this phone's copy wins.
    private func askAboutConflict(_ profileName: String) -> @Sendable () async -> ConflictChoice {
        { [weak self] in
            await withCheckedContinuation { continuation in
                Task { @MainActor in
                    guard let self else { return continuation.resume(returning: .keepOnline) }
                    self.conflict = Conflict(profileName: profileName) { choice in
                        continuation.resume(returning: choice)
                    }
                }
            }
        }
    }

    static func explain(_ error: Error) -> String {
        switch error {
        case GoogleAuthError.revoked:
            return "Google access was turned off for this app. Sign in again to reconnect."
        case GoogleAuthError.notConnected:
            return "Not signed in to Google."
        case GoogleAuthError.notConfigured, GoogleAuthError.stateMismatch, GoogleAuthError.noCode:
            return "Sign-in did not complete. Try again."
        case DriveError.notFound:
            return "That sheet is not in your Drive any more. It may have been deleted."
        case DriveError.notAuthorized:
            return "Google would not allow that. Sign in again to reconnect."
        case let DriveError.api(status, _):
            return "Google answered with an error (\(status)). Try again in a moment."
        case let error as URLError where error.code == .notConnectedToInternet:
            return "No internet connection. Your profile is still saved on this device."
        default:
            return error.localizedDescription
        }
    }
}
