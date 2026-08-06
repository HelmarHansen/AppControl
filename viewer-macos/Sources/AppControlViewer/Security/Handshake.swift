import Crypto
import Foundation

enum HandshakeRole { case host, viewer }

struct HandshakeMessage: Codable {
    var t: String = "hs"
    var v: String = "ac/1"
    var ephemeral: String       // base64, 32 Byte X25519
    var `static`: String        // base64, 32 Byte X25519 (Langzeit-Identität)
    var name: String
    var nonce: String           // base64, 16 Byte

    enum CodingKeys: String, CodingKey {
        case t, v, ephemeral, name, nonce
        case `static` = "static"
    }
}

struct HandshakeResult {
    let sessionKey: Data
    let peerStaticPublicKey: Data
    let peerName: String
    let sasEmoji: String
    let sasIndices: [Int]
}

/// Triple-Diffie-Hellman-Handshake, Struktur von Noise `KK` mit vorgeschaltetem
/// PSK-Umschlag.
///
/// Die drei DH-Operationen leisten zusammen:
/// - `dh_1` (e × e): Forward Secrecy — aufgezeichnete Sitzungen bleiben unlesbar,
///   selbst wenn später beide statischen Schlüssel kompromittiert werden
/// - `dh_2` (e × s): bindet die Sitzung an die statische Identität des Viewers
/// - `dh_3` (s × e): bindet sie an die des Hosts — beidseitige Authentifizierung
///
/// Gegenstück: `Handshake.cs`. Siehe docs/05-security.md §5.3.
enum Handshake {

    /// Phase-A-Schlüssel: leitet sich allein aus dem Pairing-Ticket ab.
    static func deriveHandshakeKey(psk: Data, roomId: Data) -> Data {
        hkdf(ikm: psk, salt: roomId, info: "ac/1 handshake", length: 32)
    }

    /// Berechnet den Sitzungsschlüssel aus den eigenen privaten und den
    /// empfangenen öffentlichen Schlüsseln.
    static func computeSession(
        role: HandshakeRole,
        ownEphemeral: Curve25519.KeyAgreement.PrivateKey,
        ownStatic: Curve25519.KeyAgreement.PrivateKey,
        peerEphemeralPublic: Data,
        peerStaticPublic: Data,
        nonceHost: Data,
        nonceViewer: Data,
        peerName: String
    ) throws -> HandshakeResult {

        let peerEph = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: peerEphemeralPublic)
        let peerStatic = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: peerStaticPublic)

        let dh1 = try ownEphemeral.sharedSecretFromKeyAgreement(with: peerEph)

        // Die Rollenabhängigkeit ist der Punkt, an dem eine Implementierung am
        // ehesten von der anderen abweicht — deshalb gibt es dafür Testvektoren.
        let dh2: SharedSecret
        let dh3: SharedSecret
        switch role {
        case .host:
            dh2 = try ownEphemeral.sharedSecretFromKeyAgreement(with: peerStatic)  // e_H × s_V
            dh3 = try ownStatic.sharedSecretFromKeyAgreement(with: peerEph)        // s_H × e_V
        case .viewer:
            dh2 = try ownStatic.sharedSecretFromKeyAgreement(with: peerEph)        // s_V × e_H
            dh3 = try ownEphemeral.sharedSecretFromKeyAgreement(with: peerStatic)  // e_V × s_H
        }

        var ikm = Data()
        dh1.withUnsafeBytes { ikm.append(contentsOf: $0) }
        dh2.withUnsafeBytes { ikm.append(contentsOf: $0) }
        dh3.withUnsafeBytes { ikm.append(contentsOf: $0) }

        // Salt-Reihenfolge ist IMMER Host zuerst, unabhängig davon, wer sendet.
        var saltInput = Data()
        saltInput.append(nonceHost)
        saltInput.append(nonceViewer)
        let salt = Data(SHA256.hash(data: saltInput))

        let sessionKey = hkdf(ikm: ikm, salt: salt, info: "ac/1 session", length: 32)
        let (emoji, indices) = computeSas(sessionKey: sessionKey)

        return HandshakeResult(
            sessionKey: sessionKey,
            peerStaticPublicKey: peerStaticPublic,
            peerName: peerName,
            sasEmoji: emoji,
            sasIndices: indices)
    }

    /// Fünf Emojis aus einer 64er-Tabelle = 30 Bit.
    ///
    /// Beide Seiten zeigen dieselben Symbole; die Nutzer vergleichen sie über
    /// einen **anderen** Kanal. Ein MitM müsste zwei verschiedene
    /// Sitzungsschlüssel etablieren, die zufällig dieselben fünf Emojis ergeben:
    /// 1 zu 2³⁰. Siehe docs/05-security.md §5.4.
    static func computeSas(sessionKey: Data) -> (emoji: String, indices: [Int]) {
        let raw = hkdf(ikm: sessionKey, salt: Data(), info: "ac/1 sas", length: 5)
        let indices = raw.map { Int($0) % 64 }
        let emoji = indices.map { EmojiTable.symbols[$0] }.joined(separator: " ")
        return (emoji, indices)
    }

    /// Anzeigefreundlicher Fingerprint eines statischen Schlüssels.
    static func fingerprint(_ staticPublicKey: Data) -> String {
        let hash = Array(SHA256.hash(data: staticPublicKey).prefix(10))
        return stride(from: 0, to: 10, by: 2)
            .map { String(format: "%02x%02x", hash[$0], hash[$0 + 1]) }
            .joined(separator: " ")
    }

    static func hkdf(ikm: Data, salt: Data, info: String, length: Int) -> Data {
        let derived = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: ikm),
            salt: salt,
            info: Data(info.utf8),
            outputByteCount: length)
        return derived.withUnsafeBytes { Data($0) }
    }
}
