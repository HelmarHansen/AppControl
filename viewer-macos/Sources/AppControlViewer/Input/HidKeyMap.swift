import Foundation

/// Abbildung macOS-`CGKeyCode` → HID Usage-ID (Usage Page 0x07).
///
/// **Warum der Umweg über HID:** macOS liefert `CGKeyCode` (an ANSI-Positionen
/// gebunden), Windows will `VK_*` oder Scancodes. Eine direkte Umrechnung müsste
/// beide Tastaturlayouts kennen und wäre bei abweichenden Layouts (US-Viewer,
/// DE-Host) falsch.
///
/// HID Usage-IDs sind der gemeinsame Standard *unterhalb* beider Systeme: `0x04`
/// ist die Taste an der Position von „A" auf einem US-Layout, unabhängig davon,
/// was daraufgedruckt ist. Der Host übersetzt HID → Scancode, und Windows wendet
/// das **Host**-Layout an.
///
/// Ergebnis: Wer auf einem US-Mac die Taste rechts neben „L" drückt, erzeugt auf
/// einem deutschen Host ein „ö" — genau wie ein Mensch an dieser Tastatur.
/// Gegenstück: `HidKeyMap.cs`. Siehe docs/04-protocol.md §4.5.
enum HidKeyMap {

    /// CGKeyCode → HID Usage. Die CGKeyCodes stammen aus `<Carbon/HIToolbox/Events.h>`.
    private static let map: [UInt16: UInt16] = [
        // Buchstaben
        0x00: 0x04,  // A
        0x0B: 0x05,  // B
        0x08: 0x06,  // C
        0x02: 0x07,  // D
        0x0E: 0x08,  // E
        0x03: 0x09,  // F
        0x05: 0x0A,  // G
        0x04: 0x0B,  // H
        0x22: 0x0C,  // I
        0x26: 0x0D,  // J
        0x28: 0x0E,  // K
        0x25: 0x0F,  // L
        0x2E: 0x10,  // M
        0x2D: 0x11,  // N
        0x1F: 0x12,  // O
        0x23: 0x13,  // P
        0x0C: 0x14,  // Q
        0x0F: 0x15,  // R
        0x01: 0x16,  // S
        0x11: 0x17,  // T
        0x20: 0x18,  // U
        0x09: 0x19,  // V
        0x0D: 0x1A,  // W
        0x07: 0x1B,  // X
        0x10: 0x1C,  // Y
        0x06: 0x1D,  // Z

        // Ziffernreihe
        0x12: 0x1E, 0x13: 0x1F, 0x14: 0x20, 0x15: 0x21, 0x17: 0x22,
        0x16: 0x23, 0x1A: 0x24, 0x1C: 0x25, 0x19: 0x26, 0x1D: 0x27,

        0x24: 0x28,  // Return
        0x35: 0x29,  // Escape
        0x33: 0x2A,  // Delete (Backspace)
        0x30: 0x2B,  // Tab
        0x31: 0x2C,  // Leertaste
        0x1B: 0x2D,  // -
        0x18: 0x2E,  // =
        0x21: 0x2F,  // [
        0x1E: 0x30,  // ]
        0x2A: 0x31,  // \
        0x29: 0x33,  // ;
        0x27: 0x34,  // '
        0x32: 0x35,  // `
        0x2B: 0x36,  // ,
        0x2F: 0x37,  // .
        0x2C: 0x38,  // /
        0x39: 0x39,  // Feststelltaste

        // Funktionstasten
        0x7A: 0x3A, 0x78: 0x3B, 0x63: 0x3C, 0x76: 0x3D, 0x60: 0x3E, 0x61: 0x3F,
        0x62: 0x40, 0x64: 0x41, 0x65: 0x42, 0x6D: 0x43, 0x67: 0x44, 0x6F: 0x45,

        // Navigation
        0x72: 0x49,  // Hilfe/Einfg
        0x73: 0x4A,  // Pos1
        0x74: 0x4B,  // Bild auf
        0x75: 0x4C,  // Entf vorwärts
        0x77: 0x4D,  // Ende
        0x79: 0x4E,  // Bild ab
        0x7C: 0x4F,  // rechts
        0x7B: 0x50,  // links
        0x7D: 0x51,  // runter
        0x7E: 0x52,  // hoch

        // Ziffernblock
        0x47: 0x53, 0x4B: 0x54, 0x43: 0x55, 0x4E: 0x56, 0x45: 0x57, 0x4C: 0x58,
        0x53: 0x59, 0x54: 0x5A, 0x55: 0x5B, 0x56: 0x5C, 0x57: 0x5D, 0x58: 0x5E,
        0x59: 0x5F, 0x5B: 0x60, 0x5C: 0x61, 0x52: 0x62, 0x41: 0x63,

        0x0A: 0x64,  // ISO-Taste (§/±  bzw. <>| auf DE-Layouts)

        // Modifier
        0x3B: 0xE0,  // linke Control
        0x38: 0xE1,  // linke Shift
        0x3A: 0xE2,  // linke Option  → Alt
        0x37: 0xE3,  // linke Command → Windows-Taste
        0x3E: 0xE4,  // rechte Control
        0x3C: 0xE5,  // rechte Shift
        0x3D: 0xE6,  // rechte Option
        0x36: 0xE7,  // rechte Command
    ]

    static func hidUsage(forKeyCode keyCode: UInt16) -> UInt16? {
        map[keyCode]
    }

    /// Nur für Tests: Anzahl der abgebildeten Tasten.
    static var count: Int { map.count }

    /// Nur für Tests: Rückabbildung, um Eindeutigkeit prüfen zu können.
    static var allMappings: [UInt16: UInt16] { map }

    // TODO(Erweiterung): Consumer-Page-Usages (0x0C) für Medientasten —
    // Lautstärke, Play/Pause, Helligkeit. Diese kommen auf macOS als
    // `NSEvent.EventType.systemDefined` mit Subtype 8 und brauchen eine eigene
    // Behandlung in InputCapture, bevor sie hier abgebildet werden können.
}
