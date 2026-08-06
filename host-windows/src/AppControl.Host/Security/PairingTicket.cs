using System.Security.Cryptography;
using System.Text;

namespace AppControl.Host.Security;

/// <summary>
/// Das Einmal-Ticket, das der Host erzeugt und dem Viewer ueber einen bestehenden
/// sicheren Kanal (Signal, iMessage, Telefon, QR-Code) uebermittelt.
///
///   roomId (16 B)  oeffentlich - der Signaling-Server sieht sie
///   psk    (32 B)  geheim     - der Server sieht sie NIE
///
/// WARUM 256 BIT UND NICHT "123456": Ein sechsstelliger Code hat ~20 Bit Entropie.
/// Wird er zum Ableiten eines Verschluesselungsschluessels verwendet, kann ein
/// Angreifer, der einen einzigen Handshake-Umschlag mitliest, OFFLINE alle 10^6
/// Moeglichkeiten durchprobieren - Sekunden auf einem Laptop. Kurze Codes sind nur
/// mit einem PAKE sicher, und ein selbstgeschriebener PAKE ist eine bekannte
/// Quelle katastrophaler Fehler. Siehe docs/05-security.md §5.2.
/// </summary>
public sealed record PairingTicket(byte[] RoomId, byte[] Psk)
{
    public const int RoomIdSize = 16;
    public const int PskSize = 32;

    /// <summary>Crockford Base32: ohne I, L, O, U - keine Verwechslung von 0/O und 1/I/L.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static PairingTicket Generate()
    {
        var roomId = RandomNumberGenerator.GetBytes(RoomIdSize);
        var psk = RandomNumberGenerator.GetBytes(PskSize);
        return new PairingTicket(roomId, psk);
    }

    /// <summary>
    /// Raum-ID in der Form, die im WebSocket-JOIN steht. Base64-URL, weil der
    /// Server ein Alphabet erwartet, das sich gefahrlos loggen laesst.
    /// </summary>
    public string RoomIdForSignaling() =>
        Convert.ToBase64String(RoomId).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Menschenlesbare Form, in Vierergruppen. Die Gruppierung ist wichtig: Der
    /// Code wird per Chat verschickt oder am Telefon vorgelesen, und ein
    /// 77-Zeichen-Block ohne Struktur wird fehlerhaft uebertragen.
    /// </summary>
    public string ToDisplayString()
    {
        var payload = new byte[RoomIdSize + PskSize];
        RoomId.CopyTo(payload, 0);
        Psk.CopyTo(payload, RoomIdSize);

        var encoded = Base32Encode(payload);
        var checksum = Checksum(payload);

        var sb = new StringBuilder("AC1-");
        for (var i = 0; i < encoded.Length; i += 4)
        {
            if (i > 0) sb.Append('-');
            sb.Append(encoded, i, Math.Min(4, encoded.Length - i));
        }
        sb.Append('-').Append(checksum);
        return sb.ToString();
    }

    /// <summary>
    /// Parst ein eingegebenes Ticket. Gibt null zurueck bei Praefix-, Laengen-,
    /// Zeichen- oder Pruefsummenfehler.
    ///
    /// Die Pruefsumme ist der Grund, warum ein Tippfehler sofort als
    /// "Code falsch eingegeben" erkennbar ist statt als "Handshake
    /// fehlgeschlagen" - eine Fehlermeldung, die dem Nutzer nichts sagt und
    /// leicht mit einem Angriff verwechselt wird.
    /// </summary>
    public static PairingTicket? TryParse(string input)
    {
        var cleaned = new string(input
            .ToUpperInvariant()
            .Replace("AC1-", "")
            .Where(c => Alphabet.Contains(c))
            .ToArray());

        const int payloadChars = (RoomIdSize + PskSize) * 8 / 5 + 1;   // 77
        if (cleaned.Length < payloadChars + 2) return null;

        var body = cleaned[..payloadChars];
        var checksumPart = cleaned[payloadChars..(payloadChars + 2)];

        byte[] payload;
        try { payload = Base32Decode(body, RoomIdSize + PskSize); }
        catch { return null; }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Checksum(payload)),
                Encoding.ASCII.GetBytes(checksumPart)))
            return null;

        return new PairingTicket(payload[..RoomIdSize], payload[RoomIdSize..]);
    }

    private static string Checksum(byte[] payload)
    {
        var hash = SHA256.HashData(payload);
        return $"{Alphabet[hash[0] % 32]}{Alphabet[hash[1] % 32]}";
    }

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }
        if (bitsLeft > 0) sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string encoded, int expectedBytes)
    {
        var result = new List<byte>(expectedBytes);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in encoded)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"Ungueltiges Zeichen '{c}'");
            buffer = (buffer << 5) | value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                result.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }
        if (result.Count < expectedBytes) throw new FormatException("Ticket zu kurz");
        return [.. result.Take(expectedBytes)];
    }

    // TODO(Erweiterung): QR-Code-Darstellung im Dashboard.
    // Der Viewer kann ihn dann mit der Kamera abfotografieren (macOS: Live Text /
    // VNDetectBarcodesRequest), was Tippfehler vollstaendig eliminiert. Inhalt
    // waere schlicht ToDisplayString(), ein QR-Encoder als NuGet-Paket reicht.
}
