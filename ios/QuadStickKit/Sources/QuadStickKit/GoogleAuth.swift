import Foundation
import CryptoKit

/// OAuth 2.0 for an installed app, PKCE, scope `drive.file` only.
///
/// The desktop app runs the same flow against the same Google project. Two
/// differences, both because this is a phone:
///   - an iOS OAuth client is public, so there is no client secret to send;
///   - the redirect is a custom scheme back into the app, not a loopback port.
///
/// `drive.file` means Google only ever hands over files this app made or the
/// user explicitly picked. A profile of somebody else's is not reachable with
/// this token, which is the point.
public enum GoogleAuthError: Error, Equatable {
    /// The refresh token was revoked or expired. The user has to sign in again.
    case revoked
    /// Signed out, or never signed in.
    case notConnected
    case stateMismatch
    case noCode
    case server(status: Int, message: String)
    /// No client id was compiled in. See GoogleClient.swift.
    case notConfigured
}

public struct GoogleTokens: Equatable, Sendable {
    public let accessToken: String
    public let expiry: Date
    public let refreshToken: String?
}

public actor GoogleAuth {
    public static let scope = "https://www.googleapis.com/auth/drive.file"
    static let authEndpoint = "https://accounts.google.com/o/oauth2/v2/auth"
    static let tokenEndpoint = "https://oauth2.googleapis.com/token"

    private let clientID: String
    private let redirectURI: String
    private let http: HTTPFetching
    private let store: TokenStoring
    private let now: @Sendable () -> Date

    private var cached: (token: String, expiry: Date)?

    public init(clientID: String = GoogleClient.id,
                redirectURI: String = GoogleClient.redirectURI,
                http: HTTPFetching = URLSessionHTTP(),
                store: TokenStoring,
                now: @escaping @Sendable () -> Date = { Date() }) {
        self.clientID = clientID
        self.redirectURI = redirectURI
        self.http = http
        self.store = store
        self.now = now
    }

    public var isConfigured: Bool { GoogleClient.isConfigured(clientID) }
    public var isSignedIn: Bool { store.load() != nil }

    // MARK: - PKCE

    /// 32 random bytes, base64url. Same shape the desktop uses.
    public static func makeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    public static func challenge(for verifier: String) -> String {
        base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
    }

    static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    // MARK: - The sign-in URL

    public nonisolated func authorizationURL(challenge: String, state: String) -> URL {
        var c = URLComponents(string: Self.authEndpoint)!
        c.queryItems = [
            .init(name: "client_id", value: clientID),
            .init(name: "redirect_uri", value: redirectURI),
            .init(name: "response_type", value: "code"),
            .init(name: "scope", value: Self.scope),
            .init(name: "code_challenge", value: challenge),
            .init(name: "code_challenge_method", value: "S256"),
            .init(name: "state", value: state),
            // Without both of these Google returns no refresh token on a repeat
            // sign-in, and the app silently loses Drive the next time the access
            // token ages out.
            .init(name: "access_type", value: "offline"),
            .init(name: "prompt", value: "consent"),
        ]
        return c.url!
    }

    /// Pull the code out of the redirect, refusing anything whose state does
    /// not match the one we sent. A mismatch means the callback is not the one
    /// this app started.
    public nonisolated func code(from callback: URL, expectedState: String) throws -> String {
        let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        if let error = value("error") { throw GoogleAuthError.server(status: 0, message: error) }
        guard value("state") == expectedState else { throw GoogleAuthError.stateMismatch }
        guard let code = value("code"), !code.isEmpty else { throw GoogleAuthError.noCode }
        return code
    }

    // MARK: - Tokens

    public func exchange(code: String, verifier: String) async throws {
        let tokens = try await postToken([
            "client_id": clientID,
            "code": code,
            "code_verifier": verifier,
            "grant_type": "authorization_code",
            "redirect_uri": redirectURI,
        ])
        cached = (tokens.accessToken, tokens.expiry)
        // Google only sends the refresh token on the first consent. Keeping the
        // old one when none comes back is what lets a re-consent not sign the
        // user out.
        if let refresh = tokens.refreshToken { try store.save(refresh) }
    }

    /// A live access token, refreshed when the cached one is close to stale.
    /// The minute of slack keeps a request from starting on a token that dies
    /// while it is in flight.
    public func accessToken() async throws -> String {
        if let cached, now() < cached.expiry.addingTimeInterval(-60) { return cached.token }
        guard let refresh = store.load() else { throw GoogleAuthError.notConnected }
        let tokens = try await postToken([
            "client_id": clientID,
            "refresh_token": refresh,
            "grant_type": "refresh_token",
        ])
        cached = (tokens.accessToken, tokens.expiry)
        return tokens.accessToken
    }

    public func signOut() {
        cached = nil
        store.delete()
    }

    private func postToken(_ form: [String: String]) async throws -> GoogleTokens {
        var request = URLRequest(url: URL(string: Self.tokenEndpoint)!)
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data(form.map { key, value in
            "\(Self.formEscape(key))=\(Self.formEscape(value))"
        }.sorted().joined(separator: "&").utf8)

        let (data, response) = try await http.data(for: request)
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]

        guard (200..<300).contains(response.statusCode) else {
            let error = json["error"] as? String ?? String(decoding: data, as: UTF8.self)
            // The one failure the caller has to tell apart: the user pulled
            // access in their Google account, so retrying forever is pointless
            // and the app has to ask them to sign in again.
            if error == "invalid_grant" { throw GoogleAuthError.revoked }
            throw GoogleAuthError.server(status: response.statusCode, message: error)
        }
        guard let access = json["access_token"] as? String else {
            throw GoogleAuthError.server(status: response.statusCode, message: "no access_token in the reply")
        }
        let seconds = (json["expires_in"] as? Double) ?? 3600
        return GoogleTokens(accessToken: access,
                            expiry: now().addingTimeInterval(seconds),
                            refreshToken: json["refresh_token"] as? String)
    }

    static func formEscape(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
    }
}
