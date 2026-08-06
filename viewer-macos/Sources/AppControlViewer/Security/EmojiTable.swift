import Foundation

/// Die 64 Symbole der SAS-Darstellung. 6 Bit pro Emoji, 5 Emojis = 30 Bit.
///
/// AUSWAHLKRITERIEN: visuell klar unterscheidbar, auf macOS und Windows ähnlich
/// dargestellt, in beiden Sprachen leicht benennbar (die Nutzer lesen sie sich am
/// Telefon vor). Keine Flaggen, keine Hautfarben-Modifier, keine Symbole, die sich
/// zwischen Plattformen stark unterscheiden.
///
/// DIE REIHENFOLGE IST TEIL DES PROTOKOLLS. Ändert sie sich, zeigen Host und Viewer
/// unterschiedliche Symbole für denselben Schlüssel, und die Nutzer brechen eine
/// völlig gesunde Verbindung ab. Gegenstück: EmojiTable.cs und
/// tools/crypto-vectors/generate.mjs — alle drei müssen identisch sein.
public enum EmojiTable {
    public static let symbols: [String] = [
    "\u{1F419}", "\u{1F352}", "\u{1F680}", "\u{1F514}", "\u{1F3A9}", "\u{1F335}", "\u{1F418}", "\u{1F355}",
    "\u{2693}", "\u{1F3B8}", "\u{1F98A}", "\u{1F344}", "\u{1F6B2}", "\u{1F319}", "\u{1F427}", "\u{1F34B}",
    "\u{1F3F0}", "\u{1F3BA}", "\u{1F98B}", "\u{1F345}", "\u{1F682}", "\u{1F31F}", "\u{1F422}", "\u{1F347}",
    "\u{26FA}", "\u{1F3BB}", "\u{1F981}", "\u{1F349}", "\u{1F6F5}", "\u{1F308}", "\u{1F433}", "\u{1F35E}",
    "\u{1F5FF}", "\u{1F941}", "\u{1F989}", "\u{1F351}", "\u{1F681}", "\u{2600}", "\u{1F41D}", "\u{1F955}",
    "\u{1F3D4}", "\u{1F3B9}", "\u{1F99C}", "\u{1F369}", "\u{26F5}", "\u{1F33B}", "\u{1F42C}", "\u{1F9C0}",
    "\u{1F3AA}", "\u{1F3B7}", "\u{1F9A9}", "\u{1F353}", "\u{1F69C}", "\u{2744}", "\u{1F43F}", "\u{1F330}",
    "\u{1F5FC}", "\u{1FA95}", "\u{1F994}", "\u{1F34D}", "\u{1F6F8}", "\u{1F340}", "\u{1F9AD}", "\u{1F968}",
    ]

    /// Wird beim Start einmal aufgerufen (siehe AppControlViewerApp.init).
    public static func validate() {
        precondition(symbols.count == 64, "SAS-Tabelle muss 64 Symbole haben")
        precondition(Set(symbols).count == 64, "SAS-Tabelle enthält Duplikate")
    }
}
