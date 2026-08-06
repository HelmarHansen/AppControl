using System.Buffers.Binary;

namespace AppControl.Host.Input;

/// <summary>Nachrichtentypen des Binaerprotokolls. Siehe docs/04-protocol.md §4.5.</summary>
public enum InputMessageType : byte
{
    MouseMove   = 0x01,
    MouseButton = 0x02,
    MouseScroll = 0x03,
    Key         = 0x04,
    Text        = 0x05,
    ReleaseAll  = 0x06,
}

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, Back = 3, Forward = 4 }

[Flags]
public enum KeyModifiers : byte { None = 0, Shift = 1, Control = 2, Alt = 4, Meta = 8 }

public abstract record InputEvent(uint Sequence)
{
    public sealed record MouseMove(uint Sequence, float X, float Y, uint TimestampMs)
        : InputEvent(Sequence);

    public sealed record MouseButtonEvent(uint Sequence, float X, float Y,
                                          MouseButton Button, bool Pressed, ushort ClickCount)
        : InputEvent(Sequence);

    public sealed record MouseScroll(uint Sequence, float X, float Y,
                                     float DeltaX, float DeltaY, bool Precise)
        : InputEvent(Sequence);

    public sealed record Key(uint Sequence, ushort HidUsage, bool Pressed,
                             KeyModifiers Modifiers, bool IsRepeat)
        : InputEvent(Sequence);

    public sealed record Text(uint Sequence, string Value) : InputEvent(Sequence);

    public sealed record ReleaseAll(uint Sequence) : InputEvent(Sequence);
}

/// <summary>
/// Dekodiert das Binaerprotokoll. Bewusst als eigene, UI-freie Klasse, damit sie
/// ohne Windows und ohne Netzwerk testbar ist - siehe tests/InputDecoderTests.cs.
///
/// Alle Mehrbyte-Werte sind little-endian (docs/04-protocol.md §4.5).
/// </summary>
public static class InputDecoder
{
    private const int HeaderSize = 6;

    /// <summary>
    /// Gibt null zurueck, wenn der Puffer nicht dekodierbar ist. Bewusst KEINE
    /// Exception: Eingehende Daten stammen von der Gegenseite, und ein fehlerhaftes
    /// Paket ist ein normaler Betriebszustand, kein Programmfehler. Der Aufrufer
    /// zaehlt Fehlversuche und trennt bei Haeufung (docs/04-protocol.md §4.8).
    /// </summary>
    public static InputEvent? TryDecode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize) return null;

        var type = (InputMessageType)buffer[0];
        // buffer[1] = flags, aktuell nur Bit 0 (aus CGEventTap). Fuer die Injektion
        // irrelevant, deshalb nicht dekodiert - Platz fuer spaetere Erweiterung.
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer[2..]);
        var body = buffer[HeaderSize..];

        return type switch
        {
            InputMessageType.MouseMove when body.Length >= 12 => new InputEvent.MouseMove(
                seq,
                BinaryPrimitives.ReadSingleLittleEndian(body),
                BinaryPrimitives.ReadSingleLittleEndian(body[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[8..])),

            InputMessageType.MouseButton when body.Length >= 12 => new InputEvent.MouseButtonEvent(
                seq,
                BinaryPrimitives.ReadSingleLittleEndian(body),
                BinaryPrimitives.ReadSingleLittleEndian(body[4..]),
                (MouseButton)body[8],
                body[9] != 0,
                BinaryPrimitives.ReadUInt16LittleEndian(body[10..])),

            InputMessageType.MouseScroll when body.Length >= 17 => new InputEvent.MouseScroll(
                seq,
                BinaryPrimitives.ReadSingleLittleEndian(body),
                BinaryPrimitives.ReadSingleLittleEndian(body[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(body[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(body[12..]),
                body[16] != 0),

            InputMessageType.Key when body.Length >= 5 => new InputEvent.Key(
                seq,
                BinaryPrimitives.ReadUInt16LittleEndian(body),
                body[2] != 0,
                (KeyModifiers)body[3],
                body[4] != 0),

            InputMessageType.Text when body.Length >= 2 => DecodeText(seq, body),

            InputMessageType.ReleaseAll => new InputEvent.ReleaseAll(seq),

            // Unbekannte Typen werden ignoriert, nicht als Fehler behandelt -
            // Vorwaertskompatibilitaet, damit ein neuerer Viewer einen aelteren
            // Host nicht zum Trennen bringt (docs/04-protocol.md §4.8).
            _ => null,
        };
    }

    private static InputEvent? DecodeText(uint seq, ReadOnlySpan<byte> body)
    {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (body.Length < 2 + len) return null;
        // Obergrenze gegen Speicherdruck durch einen boesartigen Peer. Legitime
        // Texteingaben sind kurz; alles darueber ist ein Missbrauchsversuch.
        if (len > 4096) return null;
        return new InputEvent.Text(seq, System.Text.Encoding.UTF8.GetString(body.Slice(2, len)));
    }
}
