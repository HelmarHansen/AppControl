import XCTest
@testable import AppControlViewer

/// Prüft den Binärencoder gegen die Spezifikation aus docs/04-protocol.md §4.5.
///
/// Die Byte-Layouts hier müssen exakt zu `InputDecoder.cs` auf der Host-Seite
/// passen. Ein Versatz um ein Byte würde sich als „Maus springt wild herum"
/// äußern — schwer zu diagnostizieren, wenn man nicht auf die Idee kommt, die
/// Rohbytes anzusehen.
final class InputEncoderTests: XCTestCase {

    private func readUInt16LE(_ data: Data, at offset: Int) -> UInt16 {
        UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
    }

    private func readUInt32LE(_ data: Data, at offset: Int) -> UInt32 {
        (0..<4).reduce(UInt32(0)) { $0 | (UInt32(data[offset + $1]) << (8 * UInt32($1))) }
    }

    private func readFloatLE(_ data: Data, at offset: Int) -> Float {
        Float(bitPattern: readUInt32LE(data, at: offset))
    }

    func testMouseMoveLayout() {
        let encoder = InputEncoder()
        let result = encoder.mouseMove(x: 0.25, y: 0.75, timestampMs: 1234)

        XCTAssertEqual(result.data.count, 18)
        XCTAssertEqual(result.data[0], InputMessageType.mouseMove.rawValue)
        XCTAssertEqual(readUInt32LE(result.data, at: 2), 1, "erste Sequenznummer ist 1")
        XCTAssertEqual(readFloatLE(result.data, at: 6), 0.25)
        XCTAssertEqual(readFloatLE(result.data, at: 10), 0.75)
        XCTAssertEqual(readUInt32LE(result.data, at: 14), 1234)
    }

    func testMouseMoveGoesOverUnreliableChannel() {
        // Der wichtigste Latenz-Trick des Protokolls: Mausbewegungen dürfen
        // NICHT über den reliable-ordered Kanal, weil ein Retransmit dort alle
        // nachfolgenden Nachrichten blockiert (Head-of-Line-Blocking).
        let encoder = InputEncoder()
        XCTAssertEqual(encoder.mouseMove(x: 0, y: 0, timestampMs: 0).channel, .cursor)
    }

    func testEverythingElseGoesOverReliableChannel() {
        // Umgekehrt MÜSSEN Tastendrücke zuverlässig sein: Ein verlorenes
        // "Taste losgelassen" hinterlässt eine hängende Taste auf dem Host.
        let encoder = InputEncoder()

        XCTAssertEqual(encoder.mouseButton(x: 0, y: 0, button: .left, pressed: true, clickCount: 1).channel, .reliable)
        XCTAssertEqual(encoder.mouseScroll(x: 0, y: 0, deltaX: 0, deltaY: 1, precise: true).channel, .reliable)
        XCTAssertEqual(encoder.key(hidUsage: 0x04, pressed: true, modifiers: [], isRepeat: false).channel, .reliable)
        XCTAssertEqual(encoder.releaseAll().channel, .reliable)
    }

    func testMouseButtonLayout() {
        let encoder = InputEncoder()
        let result = encoder.mouseButton(
            x: 0.5, y: 0.5, button: .right, pressed: true, clickCount: 2)

        XCTAssertEqual(result.data.count, 18)
        XCTAssertEqual(result.data[0], InputMessageType.mouseButton.rawValue)
        XCTAssertEqual(result.data[14], RemoteMouseButton.right.rawValue)
        XCTAssertEqual(result.data[15], 1)
        XCTAssertEqual(readUInt16LE(result.data, at: 16), 2)
    }

    func testKeyLayout() {
        let encoder = InputEncoder()
        let result = encoder.key(
            hidUsage: 0x04, pressed: true,
            modifiers: [.shift, .control], isRepeat: false)

        XCTAssertEqual(result.data.count, 11)
        XCTAssertEqual(result.data[0], InputMessageType.key.rawValue)
        XCTAssertEqual(readUInt16LE(result.data, at: 6), 0x04)
        XCTAssertEqual(result.data[8], 1)
        XCTAssertEqual(result.data[9], RemoteModifiers([.shift, .control]).rawValue)
        XCTAssertEqual(result.data[10], 0)
    }

    func testSequenceIsSharedAcrossChannels() {
        // Der gemeinsame Zähler erlaubt dem Host, Reihenfolgen zu rekonstruieren,
        // obwohl die Nachrichten über zwei verschiedene Kanäle ankommen.
        let encoder = InputEncoder()

        let first = encoder.mouseMove(x: 0, y: 0, timestampMs: 0)              // cursor
        let second = encoder.key(hidUsage: 0x04, pressed: true,
                                 modifiers: [], isRepeat: false)               // input
        let third = encoder.mouseMove(x: 0, y: 0, timestampMs: 0)              // cursor

        XCTAssertEqual(readUInt32LE(first.data, at: 2), 1)
        XCTAssertEqual(readUInt32LE(second.data, at: 2), 2)
        XCTAssertEqual(readUInt32LE(third.data, at: 2), 3)
    }

    func testTextIsUtf8Encoded() throws {
        let encoder = InputEncoder()
        let result = try XCTUnwrap(encoder.text("Grüße 🎉"))

        let byteLength = Int(readUInt16LE(result.data, at: 6))
        let payload = result.data[8..<(8 + byteLength)]

        XCTAssertEqual(String(data: payload, encoding: .utf8), "Grüße 🎉")
    }

    func testOversizedTextIsRejected() {
        // Obergrenze gegen Speicherdruck auf der Host-Seite.
        let encoder = InputEncoder()
        XCTAssertNil(encoder.text(String(repeating: "a", count: 5000)))
    }

    func testReleaseAllHasNoBody() {
        let encoder = InputEncoder()
        let result = encoder.releaseAll()

        XCTAssertEqual(result.data.count, 6)
        XCTAssertEqual(result.data[0], InputMessageType.releaseAll.rawValue)
    }
}
