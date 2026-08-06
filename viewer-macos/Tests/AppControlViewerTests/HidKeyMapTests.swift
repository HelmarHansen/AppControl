import XCTest
@testable import AppControlViewer

/// Prüft die CGKeyCode-zu-HID-Abbildung.
///
/// Die Abbildung ist der Grund, warum Tippen über Plattformgrenzen hinweg
/// funktioniert. Siehe docs/04-protocol.md §4.5.
final class HidKeyMapTests: XCTestCase {

    func testLetterKeysMapToSequentialHidUsages() {
        // HID 0x04–0x1D sind A–Z in alphabetischer Reihenfolge. Die CGKeyCodes
        // sind es ausdrücklich NICHT — sie folgen der physischen Tastenlage.
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x00), 0x04)   // A
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x0B), 0x05)   // B
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x08), 0x06)   // C
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x06), 0x1D)   // Z
    }

    func testCommandMapsToWindowsKey() {
        // Cmd → GUI-Usage → auf dem Host die Windows-Taste. Genau deshalb steht
        // sie bei App-Freigabe auf der Blocklist (Gate 5): Sie würde das
        // Startmenü öffnen und damit den freigegebenen Bereich verlassen.
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x37), 0xE3)   // linke Command
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x36), 0xE7)   // rechte Command
    }

    func testOptionMapsToAlt() {
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x3A), 0xE2)   // linke Option → Alt
    }

    func testArrowKeysAreMapped() {
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x7C), 0x4F)   // rechts
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x7B), 0x50)   // links
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x7D), 0x51)   // runter
        XCTAssertEqual(HidKeyMap.hidUsage(forKeyCode: 0x7E), 0x52)   // hoch
    }

    func testUnknownKeyCodeReturnsNil() {
        XCTAssertNil(HidKeyMap.hidUsage(forKeyCode: 0xFFFF))
    }

    func testMappingIsInjective() {
        // Zwei CGKeyCodes, die auf dieselbe HID-Usage zeigen, wären ein Fehler:
        // Eine Taste würde eine andere auslösen.
        let usages = HidKeyMap.allMappings.values
        XCTAssertEqual(Set(usages).count, usages.count,
                       "Jede HID-Usage darf höchstens einmal vergeben sein")
    }

    func testAllLettersAndDigitsAreCovered() {
        let letterAndDigitUsages = Set((0x04...0x27).map { UInt16($0) })
        let mapped = Set(HidKeyMap.allMappings.values)

        XCTAssertTrue(letterAndDigitUsages.isSubset(of: mapped),
                      "Buchstaben und Ziffern müssen vollständig abgebildet sein")
    }
}
