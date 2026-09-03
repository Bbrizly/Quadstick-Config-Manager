import Foundation

/// The app's Google OAuth client.
///
/// An iOS OAuth client is public: there is no secret, and the whole credential
/// is the client id. It has to be in Info.plist regardless, because iOS also
/// needs the matching URL scheme registered there to receive the sign-in
/// callback, so that is the one place it lives. Nothing is compiled in and
/// nothing is committed.
///
/// To connect a build:
///   1. Google Cloud console, same project as the desktop app, Credentials,
///      create an OAuth client of type iOS with this app's bundle id
///      (`dev.bassamkamal.sipstudio`).
///   2. Put the client id in Info.plist under `GIDClientID`.
///   3. Register the reversed id as a URL scheme in `CFBundleURLTypes`.
/// Until then `isConfigured` is false and the app says Drive is not set up
/// rather than failing at the first request.
public enum GoogleClient {
    public static let infoKey = "GIDClientID"

    public static var id: String {
        (Bundle.main.object(forInfoDictionaryKey: infoKey) as? String)?
            .trimmingCharacters(in: .whitespaces) ?? ""
    }

    public static func isConfigured(_ clientID: String) -> Bool {
        !clientID.isEmpty && clientID.hasSuffix(".apps.googleusercontent.com")
    }

    /// Google's convention for an installed iOS app: the client id with its
    /// parts reversed, used as a private URL scheme.
    /// `123-abc.apps.googleusercontent.com` becomes
    /// `com.googleusercontent.apps.123-abc`.
    public static func reversedScheme(_ clientID: String) -> String? {
        guard isConfigured(clientID) else { return nil }
        let head = String(clientID.dropLast(".apps.googleusercontent.com".count))
        return "com.googleusercontent.apps.\(head)"
    }

    public static var redirectURI: String {
        guard let scheme = reversedScheme(id) else { return "" }
        return "\(scheme):/oauth2redirect"
    }
}
