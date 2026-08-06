using System.Text;
using System.Text.Json;
using AppControl.Host.Security;
using NSec.Cryptography;
using Xunit;

namespace AppControl.Host.Tests;

/// <summary>
/// Prueft die C#-Kryptografie gegen die gemeinsamen Testvektoren aus
/// tools/crypto-vectors/vectors.json.
///
/// WARUM DAS WICHTIG IST: Dieselbe Krypto wird dreimal implementiert - in C#, in
/// Swift und in der Node-Referenz. Zwei unabhaengige Implementierungen einer
/// Spezifikation weichen erfahrungsgemaess in genau den Details voneinander ab,
/// die niemand testet: Byte-Reihenfolge des Zaehlers, Reihenfolge der Nonces im
/// Salt, Zuordnung der drei DH-Operationen zu den Rollen.
///
/// Ohne diese Tests aeussert sich so eine Abweichung als "Verbindung schlaegt
/// fehl, Fehlermeldung unbrauchbar" beim Nutzer. Mit ihnen als fehlschlagender
/// Test mit klarer Ursache.
/// </summary>
public sealed class CryptoVectorTests
{
    private static readonly JsonDocument Vectors = LoadVectors();

    private static JsonDocument LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vectors.json");
        Assert.True(File.Exists(path),
            $"vectors.json fehlt unter {path}. " +
            "Erzeugen mit: node tools/crypto-vectors/generate.mjs > tools/crypto-vectors/vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static byte[] Hex(string key, params string[] path)
    {
        var element = Vectors.RootElement;
        foreach (var segment in path) element = element.GetProperty(segment);
        return Convert.FromHexString(element.GetProperty(key).GetString()!);
    }

    [Fact]
    public void HandshakeKey_stimmt_mit_der_Referenz_ueberein()
    {
        var psk = Hex("psk", "pairing");
        var roomId = Hex("roomId", "pairing");
        var expected = Hex("handshakeKey", "pairing");

        var actual = Handshake.DeriveHandshakeKey(psk, roomId);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SessionKey_Host_stimmt_mit_der_Referenz_ueberein()
    {
        var result = ComputeAs(HandshakeRole.Host);
        Assert.Equal(Hex("sessionKey", "handshake"), result.SessionKey);
    }

    [Fact]
    public void SessionKey_Viewer_ergibt_denselben_Schluessel_wie_Host()
    {
        // Die zentrale Eigenschaft des Handshakes: Zwei Seiten mit
        // UNTERSCHIEDLICHEN privaten Schluesseln kommen auf DENSELBEN
        // Sitzungsschluessel.
        var host = ComputeAs(HandshakeRole.Host);
        var viewer = ComputeAs(HandshakeRole.Viewer);

        Assert.Equal(host.SessionKey, viewer.SessionKey);
    }

    private static HandshakeResult ComputeAs(HandshakeRole role)
    {
        var x = Vectors.RootElement.GetProperty("x25519");
        byte[] Get(string name) => Convert.FromHexString(x.GetProperty(name).GetString()!);

        var (ephPriv, staticPriv, peerEph, peerStatic) = role == HandshakeRole.Host
            ? (Get("hostEphPriv"), Get("hostStaticPriv"), Get("viewerEphPub"), Get("viewerStaticPub"))
            : (Get("viewerEphPriv"), Get("viewerStaticPriv"), Get("hostEphPub"), Get("hostStaticPub"));

        var import = (byte[] raw) => Key.Import(
            KeyAgreementAlgorithm.X25519, raw, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        return Handshake.ComputeSession(
            role, import(ephPriv), import(staticPriv), peerEph, peerStatic,
            nonceHost: Hex("nonceHost", "handshake"),
            nonceViewer: Hex("nonceViewer", "handshake"),
            peerName: "test");
    }

    [Fact]
    public void SAS_stimmt_in_Indizes_und_Symbolen_mit_der_Referenz_ueberein()
    {
        var sessionKey = Hex("sessionKey", "handshake");
        var expectedIndices = Vectors.RootElement.GetProperty("sas").GetProperty("indices")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var expectedEmoji = Vectors.RootElement.GetProperty("sas").GetProperty("emoji")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

        var (emoji, indices) = Handshake.ComputeSas(sessionKey);

        Assert.Equal(expectedIndices, indices);
        Assert.Equal(string.Join(' ', expectedEmoji), emoji);
    }

    [Fact]
    public void EmojiTabelle_hat_64_eindeutige_Symbole()
    {
        // Die Reihenfolge ist Teil des Protokolls - eine Aenderung wuerde dazu
        // fuehren, dass Host und Viewer verschiedene Symbole fuer denselben
        // Schluessel zeigen und die Nutzer eine gesunde Verbindung abbrechen.
        Assert.Equal(64, EmojiTable.Symbols.Length);
        Assert.Equal(64, EmojiTable.Symbols.Distinct().Count());
    }

    [Fact]
    public void Umschlag_reproduziert_den_Referenz_Ciphertext_bitgenau()
    {
        var key = Hex("sessionKey", "handshake");
        var env = Vectors.RootElement.GetProperty("envelope");
        var salt4 = Convert.FromHexString(env.GetProperty("salt4").GetString()!);
        var plaintext = Convert.FromBase64String(env.GetProperty("plaintext").GetString()!);
        var expected = Convert.FromBase64String(env.GetProperty("sealed").GetString()!);

        var channel = new SecureChannel(key, ChannelPhase.Session, ChannelDirection.HostToViewer);
        var actual = channel.SealWithFixedNonce(plaintext, counter: 1, salt4);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Umschlag_der_Gegenrichtung_laesst_sich_oeffnen()
    {
        var key = Hex("sessionKey", "handshake");
        var expected = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("plaintext").GetString()!);
        var sealedBytes = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("sealed").GetString()!);

        // Der Referenzumschlag ist host->viewer; ihn oeffnet ein Kanal, der
        // selbst viewer->host sendet.
        var viewerSide = new SecureChannel(key, ChannelPhase.Session, ChannelDirection.ViewerToHost);

        Assert.Equal(expected, viewerSide.Open(sealedBytes));
    }

    [Fact]
    public void Manipulierter_Ciphertext_wird_abgewiesen()
    {
        var key = Hex("sessionKey", "handshake");
        var tampered = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("sealed").GetString()!);
        tampered[25] ^= 0x01;

        var viewerSide = new SecureChannel(key, ChannelPhase.Session, ChannelDirection.ViewerToHost);

        Assert.Null(viewerSide.Open(tampered));
    }

    [Fact]
    public void Gefaelschte_Richtung_im_Header_wird_abgewiesen()
    {
        // Der Header ist Associated Data der AEAD. Eine geaenderte Richtung
        // bricht die Authentifizierung - das verhindert Reflection-Angriffe,
        // bei denen eine an uns gesendete Nachricht zurueckgespiegelt wird.
        var key = Hex("sessionKey", "handshake");
        var tampered = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("sealed").GetString()!);
        tampered[5] = (byte)ChannelDirection.ViewerToHost;

        var viewerSide = new SecureChannel(key, ChannelPhase.Session, ChannelDirection.ViewerToHost);

        Assert.Null(viewerSide.Open(tampered));
    }

    [Fact]
    public void Replay_desselben_Umschlags_wird_abgewiesen()
    {
        var key = Hex("sessionKey", "handshake");
        var sealedBytes = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("sealed").GetString()!);

        var viewerSide = new SecureChannel(key, ChannelPhase.Session, ChannelDirection.ViewerToHost);

        Assert.NotNull(viewerSide.Open(sealedBytes));
        Assert.Null(viewerSide.Open(sealedBytes));   // zweites Mal: Replay
    }

    [Fact]
    public void Falscher_Schluessel_oeffnet_den_Umschlag_nicht()
    {
        var wrong = Hex("sessionKey", "handshake");
        wrong[31] ^= 0xFF;
        var sealedBytes = Convert.FromBase64String(
            Vectors.RootElement.GetProperty("envelope").GetProperty("sealed").GetString()!);

        var channel = new SecureChannel(wrong, ChannelPhase.Session, ChannelDirection.ViewerToHost);

        Assert.Null(channel.Open(sealedBytes));
    }
}
