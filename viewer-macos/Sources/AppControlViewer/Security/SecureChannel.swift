import Crypto
import Foundation

enum ChannelPhase: UInt8 { case handshake = 0x01, session = 0x02 }
enum ChannelDirection: UInt8 { case hostToViewer = 0x01, viewerToHost = 0x02 }

/// Ende-zu-Ende-Verschlüsselung des Signalings.
///
/// Swift-Gegenstück zu `Security/SecureChannel.cs` und
/// `tools/crypto-vectors/generate.mjs`. Alle drei müssen bitgleiche Ergebnisse
/// liefern; `CryptoVectorTests.swift` prüft das gegen `vectors.json`.
///
/// Siehe docs/04-protocol.md §4.2.
final class SecureChannel {
    static let headerSize = 20
    static let keySize = 32
    private static let magic: [UInt8] = Array("AC1\0".utf8)

    private let key: SymmetricKey
    private let phase: ChannelPhase
    private let sendDirection: ChannelDirection
    private let sendSalt: Data

    private var sendCounter: UInt64 = 0
    private var highestReceivedCounter: UInt64 = 0
    private let lock = NSLock()

    init(key keyMaterial: Data, phase: ChannelPhase, sendDirection: ChannelDirection) {
        precondition(keyMaterial.count == Self.keySize, "Schlüssel muss 32 Byte lang sein")
        self.key = SymmetricKey(data: keyMaterial)
        self.phase = phase
        self.sendDirection = sendDirection

        var salt = Data(count: 4)
        salt.withUnsafeMutableBytes { buffer in
            _ = SecRandomCopyBytes(kSecRandomDefault, 4, buffer.baseAddress!)
        }
        self.sendSalt = salt
    }

    private var receiveDirection: ChannelDirection {
        sendDirection == .hostToViewer ? .viewerToHost : .hostToViewer
    }

    /// Verschlüsselt einen Klartext zu einem sendefertigen Umschlag.
    func seal(_ plaintext: Data) throws -> Data {
        lock.lock()
        sendCounter += 1
        let counter = sendCounter
        lock.unlock()

        return try Self.seal(key: key, phase: phase, direction: sendDirection,
                             counter: counter, salt4: sendSalt, plaintext: plaintext)
    }

    /// Interne Variante mit festem Nonce — für Testvektoren.
    static func seal(key: SymmetricKey, phase: ChannelPhase, direction: ChannelDirection,
                     counter: UInt64, salt4: Data, plaintext: Data) throws -> Data {
        let header = buildHeader(phase: phase, direction: direction, counter: counter, salt4: salt4)

        // Nonce sind die Bytes 8..20 des Headers: Zähler(8, big-endian) + Salt(4).
        // Der Zähler steigt streng monoton, der Salt ist pro Kanal zufällig —
        // damit ist eine Nonce-Wiederverwendung ausgeschlossen, die bei
        // ChaCha20-Poly1305 katastrophal wäre.
        let nonce = try ChaChaPoly.Nonce(data: header[8..<20])

        let box = try ChaChaPoly.seal(plaintext, using: key, nonce: nonce, authenticating: header)

        var envelope = Data(header)
        envelope.append(box.ciphertext)
        envelope.append(box.tag)
        return envelope
    }

    /// Entschlüsselt einen Umschlag.
    ///
    /// Gibt `nil` zurück bei fehlerhaftem Format, falscher Richtung, Replay oder
    /// gescheiterter Authentifizierung. Bewusst **kein** `throws`: Ein
    /// manipulierter Umschlag ist ein normaler Betriebszustand bei einer
    /// Verbindung über das offene Internet. Der Aufrufer zählt Fehlversuche und
    /// trennt bei Häufung (docs/04-protocol.md §4.8).
    func open(_ envelope: Data) -> Data? {
        guard envelope.count >= Self.headerSize + 16 else { return nil }

        let header = envelope.prefix(Self.headerSize)
        let bytes = [UInt8](header)

        guard Array(bytes[0..<4]) == Self.magic else { return nil }
        guard bytes[4] == phase.rawValue else { return nil }

        // Richtungsprüfung: Ohne sie könnte ein Angreifer eine an uns gesendete
        // Nachricht zurückspiegeln, und wir würden sie als Antwort des Peers
        // akzeptieren (Reflection-Angriff).
        guard bytes[5] == receiveDirection.rawValue else { return nil }

        let counter = bytes[8..<16].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }

        // Replay-Schutz: Der Zähler muss echt größer sein als alles bisher
        // Akzeptierte.
        lock.lock()
        let lastAccepted = highestReceivedCounter
        lock.unlock()
        guard counter > lastAccepted else { return nil }

        let ciphertext = envelope[(envelope.startIndex + Self.headerSize)..<(envelope.endIndex - 16)]
        let tag = envelope.suffix(16)

        do {
            let nonce = try ChaChaPoly.Nonce(data: header[(header.startIndex + 8)..<(header.startIndex + 20)])
            let box = try ChaChaPoly.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
            let plaintext = try ChaChaPoly.open(box, using: key, authenticating: Data(header))

            // Erst NACH erfolgreicher Authentifizierung fortschreiben. Sonst
            // könnte ein Angreifer mit gefälschten hohen Zählern legitime
            // Nachrichten dauerhaft aussperren.
            lock.lock()
            highestReceivedCounter = counter
            lock.unlock()

            return plaintext
        } catch {
            return nil
        }
    }

    static func buildHeader(phase: ChannelPhase, direction: ChannelDirection,
                            counter: UInt64, salt4: Data) -> Data {
        var header = Data(count: headerSize)
        header.replaceSubrange(0..<4, with: magic)
        header[4] = phase.rawValue
        header[5] = direction.rawValue
        // header[6..8] reserviert, bleibt 0
        withUnsafeBytes(of: counter.bigEndian) { header.replaceSubrange(8..<16, with: $0) }
        header.replaceSubrange(16..<20, with: salt4.prefix(4))
        return header
    }
}
