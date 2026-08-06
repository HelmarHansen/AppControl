import Crypto
import XCTest
@testable import AppControlViewer

/// Prüft die Swift-Kryptografie gegen die gemeinsamen Testvektoren aus
/// `tools/crypto-vectors/vectors.json`.
///
/// **Warum das wichtig ist:** Dieselbe Krypto wird dreimal implementiert — in C#,
/// in Swift und in der Node-Referenz. Zwei unabhängige Implementierungen einer
/// Spezifikation weichen erfahrungsgemäß in genau den Details voneinander ab, die
/// niemand testet: Byte-Reihenfolge des Zählers, Reihenfolge der Nonces im Salt,
/// Zuordnung der drei DH-Operationen zu den Rollen.
///
/// Ohne diese Tests äußert sich so eine Abweichung als „Verbindung schlägt fehl,
/// Fehlermeldung unbrauchbar" beim Nutzer. Mit ihnen als fehlschlagender Test mit
/// klarer Ursache.
final class CryptoVectorTests: XCTestCase {

    private struct Vectors: Decodable {
        struct X25519: Decodable {
            let hostStaticPriv, hostStaticPub, hostEphPriv, hostEphPub: String
            let viewerStaticPriv, viewerStaticPub, viewerEphPriv, viewerEphPub: String
        }
        struct Pairing: Decodable { let psk, roomId, handshakeKey: String }
        struct HandshakeVec: Decodable {
            let nonceHost, nonceViewer, dh1, dh2, dh3, salt, sessionKey: String
        }
        struct Sas: Decodable { let bytes: String; let indices: [Int]; let emoji: [String] }
        struct Envelope: Decodable {
            let phase, direction: Int
            let counter: Int
            let salt4, header, plaintext, sealed: String
        }

        let x25519: X25519
        let pairing: Pairing
        let handshake: HandshakeVec
        let sas: Sas
        let envelope: Envelope
    }

    private static var vectors: Vectors = {
        guard let url = Bundle.module.url(forResource: "vectors", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode(Vectors.self, from: data) else {
            fatalError("""
                vectors.json konnte nicht geladen werden.
                Erzeugen mit: node tools/crypto-vectors/generate.mjs > tools/crypto-vectors/vectors.json
                und nach viewer-macos/Tests/AppControlViewerTests/ kopieren oder verlinken.
                """)
        }
        return decoded
    }()

    private var v: Vectors { Self.vectors }

    private func hex(_ string: String) -> Data {
        var data = Data(capacity: string.count / 2)
        var index = string.startIndex
        while index < string.endIndex {
            let next = string.index(index, offsetBy: 2)
            data.append(UInt8(string[index..<next], radix: 16)!)
            index = next
        }
        return data
    }

    private func hexString(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    // ── Handshake ────────────────────────────────────────────────────────────

    func testHandshakeKeyMatchesReference() throws {
        let derived = Handshake.deriveHandshakeKey(
            psk: hex(v.pairing.psk),
            roomId: hex(v.pairing.roomId))

        XCTAssertEqual(hexString(derived), v.pairing.handshakeKey)
    }

    func testSessionKeyAsViewerMatchesReference() throws {
        let result = try computeSession(role: .viewer)
        XCTAssertEqual(hexString(result.sessionKey), v.handshake.sessionKey)
    }

    func testSessionKeyAsHostMatchesReference() throws {
        // Der Viewer berechnet in der Praxis nur die Viewer-Seite, aber beide
        // Pfade müssen stimmen — sonst würde ein Rollentausch (etwa für Tests
        // oder eine spätere bidirektionale Variante) still falsche Ergebnisse
        // liefern.
        let result = try computeSession(role: .host)
        XCTAssertEqual(hexString(result.sessionKey), v.handshake.sessionKey)
    }

    func testHostAndViewerDeriveIdenticalKey() throws {
        // Die zentrale Eigenschaft des Handshakes: Zwei Seiten mit
        // UNTERSCHIEDLICHEN privaten Schlüsseln kommen auf DENSELBEN
        // Sitzungsschlüssel.
        let host = try computeSession(role: .host)
        let viewer = try computeSession(role: .viewer)

        XCTAssertEqual(host.sessionKey, viewer.sessionKey)
    }

    private func computeSession(role: HandshakeRole) throws -> HandshakeResult {
        let x = v.x25519

        let (ephPriv, staticPriv, peerEph, peerStatic): (String, String, String, String) =
            role == .host
                ? (x.hostEphPriv, x.hostStaticPriv, x.viewerEphPub, x.viewerStaticPub)
                : (x.viewerEphPriv, x.viewerStaticPriv, x.hostEphPub, x.hostStaticPub)

        return try Handshake.computeSession(
            role: role,
            ownEphemeral: Curve25519.KeyAgreement.PrivateKey(rawRepresentation: hex(ephPriv)),
            ownStatic: Curve25519.KeyAgreement.PrivateKey(rawRepresentation: hex(staticPriv)),
            peerEphemeralPublic: hex(peerEph),
            peerStaticPublic: hex(peerStatic),
            nonceHost: hex(v.handshake.nonceHost),
            nonceViewer: hex(v.handshake.nonceViewer),
            peerName: "test")
    }

    // ── SAS ──────────────────────────────────────────────────────────────────

    func testSasMatchesReference() {
        let (emoji, indices) = Handshake.computeSas(sessionKey: hex(v.handshake.sessionKey))

        XCTAssertEqual(indices, v.sas.indices)
        XCTAssertEqual(emoji, v.sas.emoji.joined(separator: " "))
    }

    func testEmojiTableHas64UniqueSymbols() {
        // Die Reihenfolge ist Teil des Protokolls — eine Änderung würde dazu
        // führen, dass Host und Viewer verschiedene Symbole für denselben
        // Schlüssel zeigen und die Nutzer eine gesunde Verbindung abbrechen.
        XCTAssertEqual(EmojiTable.symbols.count, 64)
        XCTAssertEqual(Set(EmojiTable.symbols).count, 64)
    }

    // ── Umschlag ─────────────────────────────────────────────────────────────

    func testSealReproducesReferenceCiphertextExactly() throws {
        let key = SymmetricKey(data: hex(v.handshake.sessionKey))

        let sealed = try SecureChannel.seal(
            key: key, phase: .session, direction: .hostToViewer,
            counter: UInt64(v.envelope.counter),
            salt4: hex(v.envelope.salt4),
            plaintext: Data(base64Encoded: v.envelope.plaintext)!)

        XCTAssertEqual(sealed.base64EncodedString(), v.envelope.sealed)
    }

    func testOpenAcceptsReferenceEnvelope() {
        // Der Referenzumschlag ist host→viewer; ihn öffnet ein Kanal, der selbst
        // viewer→host sendet.
        let channel = SecureChannel(
            key: hex(v.handshake.sessionKey), phase: .session, sendDirection: .viewerToHost)

        let opened = channel.open(Data(base64Encoded: v.envelope.sealed)!)

        XCTAssertEqual(opened?.base64EncodedString(), v.envelope.plaintext)
    }

    func testTamperedCiphertextIsRejected() {
        var tampered = Data(base64Encoded: v.envelope.sealed)!
        tampered[25] ^= 0x01

        let channel = SecureChannel(
            key: hex(v.handshake.sessionKey), phase: .session, sendDirection: .viewerToHost)

        XCTAssertNil(channel.open(tampered))
    }

    func testTamperedHeaderIsRejected() {
        // Der Header ist Associated Data der AEAD. Eine geänderte Richtung bricht
        // die Authentifizierung — das verhindert Reflection-Angriffe, bei denen
        // eine an uns gesendete Nachricht zurückgespiegelt wird.
        var tampered = Data(base64Encoded: v.envelope.sealed)!
        tampered[5] = ChannelDirection.viewerToHost.rawValue

        let channel = SecureChannel(
            key: hex(v.handshake.sessionKey), phase: .session, sendDirection: .viewerToHost)

        XCTAssertNil(channel.open(tampered))
    }

    func testReplayIsRejected() {
        let channel = SecureChannel(
            key: hex(v.handshake.sessionKey), phase: .session, sendDirection: .viewerToHost)
        let envelope = Data(base64Encoded: v.envelope.sealed)!

        XCTAssertNotNil(channel.open(envelope))
        XCTAssertNil(channel.open(envelope), "Ein zweites Mal ist ein Replay")
    }

    func testWrongKeyCannotOpen() {
        var wrong = hex(v.handshake.sessionKey)
        wrong[31] ^= 0xFF

        let channel = SecureChannel(key: wrong, phase: .session, sendDirection: .viewerToHost)

        XCTAssertNil(channel.open(Data(base64Encoded: v.envelope.sealed)!))
    }
}
