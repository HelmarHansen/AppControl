import Crypto
import Foundation

/// Das Einmal-Ticket vom Host. Der Viewer parst es nur — erzeugt wird es
/// ausschließlich auf der Host-Seite.
///
/// Gegenstück: `PairingTicket.cs`. Siehe docs/05-security.md §5.2.
struct PairingTicket: Equatable {
    let roomId: Data   // 16 Byte, öffentlich
    let psk: Data      // 32 Byte, geheim

    static let roomIdSize = 16
    static let pskSize = 32

    /// Crockford Base32: ohne I, L, O, U — keine Verwechslung von 0/O und 1/I/L.
    private static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")

    /// Raum-ID in der Form, die im WebSocket-JOIN steht (Base64-URL ohne Padding).
    var roomIdForSignaling: String {
        roomId.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// Parst ein eingegebenes oder eingefügtes Ticket.
    ///
    /// Toleriert Bindestriche, Leerzeichen, Kleinschreibung und ein fehlendes
    /// „AC1-"-Präfix — der Code wird per Chat verschickt oder am Telefon
    /// vorgelesen, und jede Toleranz spart eine Fehlermeldung.
    ///
    /// Die Prüfsumme sorgt dafür, dass ein Tippfehler als „Code falsch
    /// eingegeben" erkennbar ist statt als „Handshake fehlgeschlagen" — eine
    /// Meldung, die dem Nutzer nichts sagt und leicht mit einem Angriff
    /// verwechselt wird.
    static func parse(_ input: String) -> PairingTicket? {
        let cleaned = input
            .uppercased()
            .replacingOccurrences(of: "AC1-", with: "")
            .filter { alphabet.contains($0) }

        let payloadChars = (roomIdSize + pskSize) * 8 / 5 + 1   // 77
        guard cleaned.count >= payloadChars + 2 else { return nil }

        let characters = Array(cleaned)
        let body = String(characters[0..<payloadChars])
        let checksumPart = String(characters[payloadChars..<(payloadChars + 2)])

        guard let payload = base32Decode(body, expectedBytes: roomIdSize + pskSize),
              checksum(payload) == checksumPart else { return nil }

        return PairingTicket(
            roomId: payload.prefix(roomIdSize),
            psk: payload.suffix(pskSize))
    }

    private static func checksum(_ payload: Data) -> String {
        let hash = Array(SHA256.hash(data: payload))
        return String([alphabet[Int(hash[0]) % 32], alphabet[Int(hash[1]) % 32]])
    }

    private static func base32Decode(_ encoded: String, expectedBytes: Int) -> Data? {
        var result = Data(capacity: expectedBytes)
        var buffer = 0
        var bitsLeft = 0

        for character in encoded {
            guard let value = alphabet.firstIndex(of: character) else { return nil }
            buffer = (buffer << 5) | value
            bitsLeft += 5
            if bitsLeft >= 8 {
                result.append(UInt8((buffer >> (bitsLeft - 8)) & 0xFF))
                bitsLeft -= 8
            }
        }

        guard result.count >= expectedBytes else { return nil }
        return result.prefix(expectedBytes)
    }
}
