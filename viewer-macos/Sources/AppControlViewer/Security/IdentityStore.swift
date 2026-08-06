import Crypto
import Foundation
import Security

/// Langzeit-Identität des Viewers: ein statisches X25519-Schlüsselpaar, erzeugt
/// beim ersten Start.
///
/// **Speicherung:** macOS-Keychain mit
/// `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`. Zwei Eigenschaften sind dabei
/// wichtig: Der Schlüssel ist ohne entsperrtes Gerät nicht lesbar, und
/// `ThisDeviceOnly` schließt iCloud-Synchronisierung aus — die Identität soll
/// nicht auf anderen Geräten des Nutzers landen, sonst wäre der Fingerprint
/// keine Geräteidentität mehr.
///
/// Gegenstück: `IdentityStore.cs` (dort DPAPI). Siehe docs/05-security.md §5.5.
final class IdentityStore {
    private static let service = "com.appcontrol.viewer"
    private static let account = "static-identity-x25519"

    private var cachedKey: Curve25519.KeyAgreement.PrivateKey?

    var staticKey: Curve25519.KeyAgreement.PrivateKey {
        if let cached = cachedKey { return cached }
        let key = loadOrCreate()
        cachedKey = key
        return key
    }

    var staticPublicKey: Data { staticKey.publicKey.rawRepresentation }

    var fingerprint: String { Handshake.fingerprint(staticPublicKey) }

    /// Anzeigename dieses Geräts. Wird dem Host im Handshake mitgeteilt und
    /// erscheint dort im Consent-Dialog und im Overlay.
    var deviceName: String {
        let host = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
        return "\(host) (macOS)"
    }

    private func loadOrCreate() -> Curve25519.KeyAgreement.PrivateKey {
        if let existing = loadFromKeychain(),
           let key = try? Curve25519.KeyAgreement.PrivateKey(rawRepresentation: existing) {
            return key
        }

        let newKey = Curve25519.KeyAgreement.PrivateKey()
        saveToKeychain(newKey.rawRepresentation)
        return newKey
    }

    private func loadFromKeychain() -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]

        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    private func saveToKeychain(_ data: Data) {
        // Vorhandenen Eintrag entfernen: SecItemAdd schlägt sonst mit
        // errSecDuplicateItem fehl, wenn ein unlesbarer Rest existiert.
        let deleteQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
        ]
        SecItemDelete(deleteQuery as CFDictionary)

        let addQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: Self.service,
            kSecAttrAccount as String: Self.account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly,
        ]

        let status = SecItemAdd(addQuery as CFDictionary, nil)
        if status != errSecSuccess {
            // Ohne Keychain-Zugriff funktioniert die App trotzdem — dann ist die
            // Identität pro Start neu, und der Host zeigt jedes Mal eine
            // TOFU-Warnung. Das ist unangenehm, aber sicher: mehr Vorsicht, nicht
            // weniger.
            NSLog("AppControl: Identität konnte nicht in der Keychain gespeichert werden (%d). "
                + "Der Host wird bei jeder Sitzung eine Schlüsselwarnung zeigen.", status)
        }
    }
}
