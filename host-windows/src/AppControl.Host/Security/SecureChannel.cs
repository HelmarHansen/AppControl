using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace AppControl.Host.Security;

public enum ChannelPhase : byte { Handshake = 0x01, Session = 0x02 }
public enum ChannelDirection : byte { HostToViewer = 0x01, ViewerToHost = 0x02 }

/// <summary>
/// Ende-zu-Ende-Verschluesselung des Signalings.
///
/// Diese Klasse ist das C#-Gegenstueck zu Security/SecureChannel.swift im Viewer
/// und zu tools/crypto-vectors/generate.mjs. Alle drei muessen bitgleiche
/// Ergebnisse liefern; CryptoVectorTests.cs prueft das gegen vectors.json.
///
/// Siehe docs/04-protocol.md §4.2 und docs/05-security.md §5.3.
/// </summary>
public sealed class SecureChannel
{
    public const int HeaderSize = 20;
    public const int KeySize = 32;
    private static readonly byte[] Magic = "AC1\0"u8.ToArray();

    private readonly Key _key;
    private readonly ChannelDirection _sendDirection;
    private readonly ChannelPhase _phase;
    private readonly byte[] _sendSalt = new byte[4];

    private ulong _sendCounter;
    private ulong _highestReceivedCounter;

    public SecureChannel(ReadOnlySpan<byte> keyMaterial, ChannelPhase phase, ChannelDirection sendDirection)
    {
        if (keyMaterial.Length != KeySize)
            throw new ArgumentException($"Schluessel muss {KeySize} Byte lang sein", nameof(keyMaterial));

        _key = Key.Import(AeadAlgorithm.ChaCha20Poly1305, keyMaterial, KeyBlobFormat.RawSymmetricKey);
        _phase = phase;
        _sendDirection = sendDirection;
        RandomNumberGenerator.Fill(_sendSalt);
    }

    private ChannelDirection ReceiveDirection => _sendDirection == ChannelDirection.HostToViewer
        ? ChannelDirection.ViewerToHost
        : ChannelDirection.HostToViewer;

    /// <summary>Verschluesselt einen Klartext zu einem sendefertigen Umschlag.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var counter = Interlocked.Increment(ref Unsafe_Counter());
        var header = BuildHeader(_phase, _sendDirection, counter, _sendSalt);

        var envelope = new byte[HeaderSize + plaintext.Length + 16];
        header.CopyTo(envelope);

        // Nonce sind die Bytes 8..20 des Headers: Zaehler(8, big-endian) + Salt(4).
        // Der Zaehler steigt streng monoton, der Salt ist pro Kanal zufaellig -
        // damit ist eine Nonce-Wiederverwendung ueber Sitzungen hinweg
        // ausgeschlossen, was bei ChaCha20-Poly1305 katastrophal waere.
        var nonce = new ReadOnlySpan<byte>(header, 8, 12);

        AeadAlgorithm.ChaCha20Poly1305.Encrypt(
            _key, nonce, associatedData: header, plaintext,
            envelope.AsSpan(HeaderSize));

        return envelope;
    }

    /// <summary>
    /// Entschluesselt einen Umschlag. Gibt null zurueck bei fehlerhaftem Format,
    /// falscher Richtung, Replay oder gescheiterter Authentifizierung.
    ///
    /// Bewusst KEINE Exception: Ein manipulierter Umschlag ist ein normaler
    /// Betriebszustand bei einer Verbindung ueber das offene Internet. Der
    /// Aufrufer zaehlt Fehlversuche und trennt bei Haeufung
    /// (docs/04-protocol.md §4.8).
    /// </summary>
    public byte[]? Open(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderSize + 16) return null;

        var header = envelope[..HeaderSize];

        if (!header[..4].SequenceEqual(Magic)) return null;
        if ((ChannelPhase)header[4] != _phase) return null;

        // Richtungspruefung: Ohne sie koennte ein Angreifer eine an uns gesendete
        // Nachricht zurueckspiegeln, und wir wuerden sie als Antwort des Peers
        // akzeptieren (Reflection-Angriff).
        if ((ChannelDirection)header[5] != ReceiveDirection) return null;

        var counter = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);

        // Replay-Schutz: Der Zaehler muss echt groesser sein als alles bisher
        // Akzeptierte. Ein wiederholt eingespielter Umschlag wird verworfen.
        if (counter <= _highestReceivedCounter) return null;

        var nonce = header[8..20];
        var ciphertext = envelope[HeaderSize..];
        var plaintext = new byte[ciphertext.Length - 16];

        if (!AeadAlgorithm.ChaCha20Poly1305.Decrypt(
                _key, nonce, associatedData: header, ciphertext, plaintext))
        {
            return null;
        }

        // Erst NACH erfolgreicher Authentifizierung fortschreiben. Sonst koennte
        // ein Angreifer mit gefaelschten hohen Zaehlern legitime Nachrichten
        // dauerhaft aussperren.
        _highestReceivedCounter = counter;
        return plaintext;
    }

    internal static byte[] BuildHeader(ChannelPhase phase, ChannelDirection direction,
                                       ulong counter, ReadOnlySpan<byte> salt4)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = (byte)phase;
        header[5] = (byte)direction;
        // header[6..8] reserviert, bleibt 0
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), counter);
        salt4[..4].CopyTo(header.AsSpan(16, 4));
        return header;
    }

    private ref long Unsafe_Counter()
    {
        // Interlocked braucht ein long-Feld; der Zaehler ist logisch ulong.
        // Bei 2^63 Nachrichten waere das ein Problem - das sind bei 1000/s
        // etwa 290 Millionen Jahre.
        return ref System.Runtime.CompilerServices.Unsafe.As<ulong, long>(ref _sendCounter);
    }

    /// <summary>Testhilfe: erlaubt deterministisches Versiegeln fuer Testvektoren.</summary>
    internal byte[] SealWithFixedNonce(ReadOnlySpan<byte> plaintext, ulong counter, ReadOnlySpan<byte> salt4)
    {
        var header = BuildHeader(_phase, _sendDirection, counter, salt4);
        var envelope = new byte[HeaderSize + plaintext.Length + 16];
        header.CopyTo(envelope, 0);
        AeadAlgorithm.ChaCha20Poly1305.Encrypt(
            _key, new ReadOnlySpan<byte>(header, 8, 12), header, plaintext, envelope.AsSpan(HeaderSize));
        return envelope;
    }
}
