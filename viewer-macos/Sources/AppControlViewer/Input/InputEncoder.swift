import Foundation

/// Nachrichtentypen des Binärprotokolls. Siehe docs/04-protocol.md §4.5.
enum InputMessageType: UInt8 {
    case mouseMove   = 0x01
    case mouseButton = 0x02
    case mouseScroll = 0x03
    case key         = 0x04
    case text        = 0x05
    case releaseAll  = 0x06
}

enum RemoteMouseButton: UInt8 {
    case left = 0, right = 1, middle = 2, back = 3, forward = 4
}

struct RemoteModifiers: OptionSet {
    let rawValue: UInt8
    static let shift   = RemoteModifiers(rawValue: 1 << 0)
    static let control = RemoteModifiers(rawValue: 1 << 1)
    static let alt     = RemoteModifiers(rawValue: 1 << 2)   // macOS: Option
    static let meta    = RemoteModifiers(rawValue: 1 << 3)   // macOS: Command
}

/// Welcher DataChannel eine Nachricht transportiert.
///
/// Die Trennung ist der wichtigste Latenz-Trick des Protokolls: Bei Paketverlust
/// blockiert ein Retransmit auf einem reliable-ordered Channel **alle**
/// nachfolgenden Nachrichten (Head-of-Line-Blocking). Mausbewegungen sind mit
/// Abstand die häufigsten Events — und die, bei denen ein verlorenes Paket egal
/// ist, weil das nächste es ohnehin überholt.
enum InputChannel {
    /// `ac-cursor` — unreliable, unordered. Nur reine Mausbewegungen.
    case cursor
    /// `ac-input` — reliable, ordered. Alles, wo ein verlorenes Event einen
    /// kaputten Zustand hinterlässt (etwa "Taste gedrückt" ohne "losgelassen").
    case reliable
}

struct EncodedInput {
    let data: Data
    let channel: InputChannel
}

/// Kodiert Eingaben ins Binärprotokoll.
///
/// Binär statt JSON, weil bei ~200 Events/s das Parsen und die Größe messbar
/// werden: 18 Byte statt ~120 Byte pro Mausbewegung, und kein JSON-Parser im
/// Hot Path.
///
/// Alle Mehrbyte-Werte **little-endian** (docs/04-protocol.md §4.5).
final class InputEncoder {
    private var sequence: UInt32 = 0

    /// Gemeinsamer Zähler über beide Kanäle: Der Host kann damit verlorene
    /// Cursor-Pakete erkennen und Reihenfolgen rekonstruieren, obwohl die
    /// Nachrichten über unterschiedliche Kanäle ankommen.
    private func nextSequence() -> UInt32 {
        sequence &+= 1
        return sequence
    }

    private func header(_ type: InputMessageType, fromEventTap: Bool = false) -> Data {
        var data = Data(capacity: 24)
        data.append(type.rawValue)
        data.append(fromEventTap ? 0x01 : 0x00)
        data.appendLE(nextSequence())
        return data
    }

    // ── Maus ─────────────────────────────────────────────────────────────────

    func mouseMove(x: Float, y: Float, timestampMs: UInt32) -> EncodedInput {
        var data = header(.mouseMove)
        data.appendLE(x)
        data.appendLE(y)
        data.appendLE(timestampMs)
        return EncodedInput(data: data, channel: .cursor)
    }

    func mouseButton(x: Float, y: Float, button: RemoteMouseButton,
                     pressed: Bool, clickCount: UInt16) -> EncodedInput {
        var data = header(.mouseButton)
        data.appendLE(x)
        data.appendLE(y)
        data.append(button.rawValue)
        data.append(pressed ? 1 : 0)
        data.appendLE(clickCount)
        return EncodedInput(data: data, channel: .reliable)
    }

    func mouseScroll(x: Float, y: Float, deltaX: Float, deltaY: Float,
                     precise: Bool) -> EncodedInput {
        var data = header(.mouseScroll)
        data.appendLE(x)
        data.appendLE(y)
        data.appendLE(deltaX)
        data.appendLE(deltaY)
        data.append(precise ? 1 : 0)
        return EncodedInput(data: data, channel: .reliable)
    }

    // ── Tastatur ─────────────────────────────────────────────────────────────

    func key(hidUsage: UInt16, pressed: Bool, modifiers: RemoteModifiers,
             isRepeat: Bool, fromEventTap: Bool = false) -> EncodedInput {
        var data = header(.key, fromEventTap: fromEventTap)
        data.appendLE(hidUsage)
        data.append(pressed ? 1 : 0)
        data.append(modifiers.rawValue)
        data.append(isRepeat ? 1 : 0)
        return EncodedInput(data: data, channel: .reliable)
    }

    /// Für Zeichen, die über Tastencodes nicht sinnvoll darstellbar sind:
    /// Emoji, IME-Eingaben, Zeichen aus der Zeichenpalette. Der Host injiziert
    /// sie über KEYEVENTF_UNICODE und umgeht damit das Tastaturlayout.
    func text(_ string: String) -> EncodedInput? {
        let utf8 = Data(string.utf8)
        guard utf8.count <= 4096 else { return nil }

        var data = header(.text)
        data.appendLE(UInt16(utf8.count))
        data.append(utf8)
        return EncodedInput(data: data, channel: .reliable)
    }

    /// Alle gehaltenen Tasten freigeben.
    ///
    /// Wird gesendet, wenn unser Fenster den Fokus verliert. Ohne diese Nachricht
    /// bliebe eine Taste auf dem Host hängen, wenn der Nutzer mitten im
    /// Tastendruck zu einer anderen macOS-App wechselt.
    func releaseAll() -> EncodedInput {
        EncodedInput(data: header(.releaseAll), channel: .reliable)
    }
}

// MARK: - Little-Endian-Anhängen

private extension Data {
    mutating func appendLE(_ value: UInt16) {
        withUnsafeBytes(of: value.littleEndian) { append(contentsOf: $0) }
    }
    mutating func appendLE(_ value: UInt32) {
        withUnsafeBytes(of: value.littleEndian) { append(contentsOf: $0) }
    }
    mutating func appendLE(_ value: Float) {
        withUnsafeBytes(of: value.bitPattern.littleEndian) { append(contentsOf: $0) }
    }
}
