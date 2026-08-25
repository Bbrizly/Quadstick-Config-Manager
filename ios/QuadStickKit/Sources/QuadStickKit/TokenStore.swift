import Foundation
import Security

/// Where the Google refresh token lives. It is a credential to the user's
/// Drive, so it goes in the Keychain and nowhere else. Not in UserDefaults, not
/// in the profile store, not in a file.
public protocol TokenStoring: Sendable {
    func load() -> String?
    func save(_ refreshToken: String) throws
    func delete()
}

public enum KeychainError: Error, Equatable {
    case status(OSStatus)
}

public struct KeychainTokenStore: TokenStoring {
    private let service: String
    private let account: String

    public init(service: String = "SipStudio", account: String = "google-drive") {
        self.service = service
        self.account = account
    }

    private var query: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }

    public func load() -> String? {
        var q = query
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    public func save(_ refreshToken: String) throws {
        let data = Data(refreshToken.utf8)
        // Update in place when there is already one, so a re-consent does not
        // leave two items and start reading whichever the Keychain picks.
        let update = SecItemUpdate(query as CFDictionary,
                                   [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return }
        guard update == errSecItemNotFound else { throw KeychainError.status(update) }

        var q = query
        q[kSecValueData as String] = data
        // The token is only ever used while the app is in the foreground with
        // the device unlocked, and it must never ride an iCloud backup onto
        // another device.
        q[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        let add = SecItemAdd(q as CFDictionary, nil)
        guard add == errSecSuccess else { throw KeychainError.status(add) }
    }

    public func delete() {
        SecItemDelete(query as CFDictionary)
    }
}

/// Tests and previews. Never used by the app.
public final class InMemoryTokenStore: TokenStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var token: String?

    public init(token: String? = nil) { self.token = token }

    public func load() -> String? { lock.withLock { token } }
    public func save(_ refreshToken: String) throws { lock.withLock { token = refreshToken } }
    public func delete() { lock.withLock { token = nil } }
}
