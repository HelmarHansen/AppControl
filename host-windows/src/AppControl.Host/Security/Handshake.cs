using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace AppControl.Host.Security;

/// <summary>Rolle im Handshake. Bestimmt die Zuordnung der DH-Operationen.</summary>
public enum HandshakeRole { Host, Viewer }

public sealed record HandshakeMessage
{
    [JsonPropertyName("t")]         public string Type { get; init; } = "hs";
    [JsonPropertyName("v")]         public string Version { get; init; } = "ac/1";
    [JsonPropertyName("ephemeral")] public string EphemeralPublicKey { get; init; } = "";
    [JsonPropertyName("static")]    public string StaticPublicKey { get; init; } = "";
    [JsonPropertyName("name")]      public string Name { get; init; } = "";
    [JsonPropertyName("nonce")]     public string Nonce { get; init; } = "";
}

public sealed record HandshakeResult(
    byte[] SessionKey,
    byte[] PeerStaticPublicKey,
    string PeerName,
    string SasEmoji,
    int[] SasIndices);

/// <summary>
/// Triple-Diffie-Hellman-Handshake, Struktur von Noise KK mit vorgeschaltetem
/// PSK-Umschlag.
///
/// Die drei DH-Operationen leisten zusammen:
///   dh_1 (e x e)  Forward Secrecy - aufgezeichnete Sitzungen bleiben unlesbar,
///                 selbst wenn spaeter beide statischen Schluessel kompromittiert
///                 werden
///   dh_2 (e x s)  bindet die Sitzung an die statische Identitaet des Viewers
///   dh_3 (s x e)  bindet sie an die des Hosts - beidseitige Authentifizierung
///
/// Siehe docs/05-security.md §5.3. Gegenstueck: Security/Handshake.swift.
/// </summary>
public static class Handshake
{
    private static readonly KeyAgreementAlgorithm X25519 = KeyAgreementAlgorithm.X25519;

    /// <summary>Phase-A-Schluessel: leitet sich allein aus dem Pairing-Ticket ab.</summary>
    public static byte[] DeriveHandshakeKey(ReadOnlySpan<byte> psk, ReadOnlySpan<byte> roomId)
        => Hkdf(psk, roomId, "ac/1 handshake", 32);

    /// <summary>
    /// Berechnet den Sitzungsschluessel aus den eigenen privaten und den
    /// empfangenen oeffentlichen Schluesseln.
    /// </summary>
    public static HandshakeResult ComputeSession(
        HandshakeRole role,
        Key ownEphemeral,
        Key ownStatic,
        ReadOnlySpan<byte> peerEphemeralPublic,
        ReadOnlySpan<byte> peerStaticPublic,
        ReadOnlySpan<byte> nonceHost,
        ReadOnlySpan<byte> nonceViewer,
        string peerName)
    {
        var peerEph = PublicKey.Import(X25519, peerEphemeralPublic, KeyBlobFormat.RawPublicKey);
        var peerStatic = PublicKey.Import(X25519, peerStaticPublic, KeyBlobFormat.RawPublicKey);

        var dh1 = Agree(ownEphemeral, peerEph);

        // Die Rollenabhaengigkeit ist der Punkt, an dem eine Implementierung am
        // ehesten von der anderen abweicht - deshalb gibt es dafuer Testvektoren.
        byte[] dh2, dh3;
        if (role == HandshakeRole.Host)
        {
            dh2 = Agree(ownEphemeral, peerStatic);   // e_H x s_V
            dh3 = Agree(ownStatic, peerEph);         // s_H x e_V
        }
        else
        {
            dh2 = Agree(ownStatic, peerEph);         // s_V x e_H  ==  e_H x s_V
            dh3 = Agree(ownEphemeral, peerStatic);   // e_V x s_H  ==  s_H x e_V
        }

        var ikm = new byte[96];
        dh1.CopyTo(ikm, 0);
        dh2.CopyTo(ikm, 32);
        dh3.CopyTo(ikm, 64);

        // Salt-Reihenfolge ist IMMER Host zuerst, unabhaengig davon, wer sendet.
        var salt = SHA256.HashData([.. nonceHost, .. nonceViewer]);

        var sessionKey = Hkdf(ikm, salt, "ac/1 session", 32);

        CryptographicOperations.ZeroMemory(dh1);
        CryptographicOperations.ZeroMemory(dh2);
        CryptographicOperations.ZeroMemory(dh3);
        CryptographicOperations.ZeroMemory(ikm);

        var (emoji, indices) = ComputeSas(sessionKey);

        return new HandshakeResult(
            sessionKey, peerStaticPublic.ToArray(), peerName, emoji, indices);
    }

    private static byte[] Agree(Key ownPrivate, PublicKey peerPublic)
    {
        var shared = X25519.Agree(ownPrivate, peerPublic)
            ?? throw new CryptographicException(
                "X25519-Schluesselaustausch fehlgeschlagen - ungueltiger oeffentlicher Schluessel " +
                "(Punkt kleiner Ordnung?). Die Verbindung wird abgebrochen.");

        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    /// <summary>
    /// Fuenf Emojis aus einer 64er-Tabelle = 30 Bit. Beide Seiten zeigen dieselben
    /// Symbole; die Nutzer vergleichen sie ueber einen ANDEREN Kanal.
    /// Ein MitM muesste zwei verschiedene Sitzungsschluessel etablieren, die
    /// zufaellig dieselben fuenf Emojis ergeben: 1 zu 2^30.
    /// Siehe docs/05-security.md §5.4.
    /// </summary>
    public static (string Emoji, int[] Indices) ComputeSas(ReadOnlySpan<byte> sessionKey)
    {
        var raw = Hkdf(sessionKey, ReadOnlySpan<byte>.Empty, "ac/1 sas", 5);
        var indices = new int[5];
        var sb = new StringBuilder();
        for (var i = 0; i < 5; i++)
        {
            indices[i] = raw[i] % 64;
            if (i > 0) sb.Append(' ');
            sb.Append(EmojiTable.Symbols[indices[i]]);
        }
        return (sb.ToString(), indices);
    }

    /// <summary>Anzeigefreundlicher Fingerprint eines statischen Schluessels.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> staticPublicKey)
    {
        var hash = SHA256.HashData(staticPublicKey);
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            if (i > 0 && i % 2 == 0) sb.Append(' ');
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }

    internal static byte[] Hkdf(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, string info, int length)
    {
        // HKDF.DeriveKey hat zwei Ueberladungen: eine mit byte[] und
        // Rueckgabewert, eine mit Spans und Ausgabepuffer. Mischen geht nicht -
        // unsere Parameter sind Spans, also die Span-Variante.
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, salt, Encoding.UTF8.GetBytes(info));
        return output;
    }

    public static Key GenerateEphemeral()
        => Key.Create(X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });

    public static byte[] PublicKeyBytes(Key key)
        => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
