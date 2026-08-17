using AppControl.Host.Media;
using Xunit;

namespace AppControl.Host.Tests;

/// <summary>
/// Prueft die Annex-B-Auswertung des Encoders.
///
/// WARUM DAS EINEN TEST WERT IST: Von dieser Erkennung haengt ab, ob ein Frame
/// als Keyframe an WebRTC gemeldet wird - und ob SPS/PPS vorangestellt werden.
/// Liegt sie falsch, sieht der Viewer entweder nie ein Bild (Parametersaetze
/// fehlen) oder WebRTC verwirft Wiederherstellungspunkte. Beides aeussert sich
/// als "schwarzes Bild" und ist von aussen kaum von einem Netzproblem zu
/// unterscheiden.
///
/// Der Encoder selbst laesst sich hier nicht testen: Er braucht eine GPU und
/// eine Capture-Quelle. Die Bitstream-Auswertung ist der Teil, der ohne beides
/// auskommt - und der Teil, in dem sich Fehler still fortpflanzen.
/// </summary>
public class AnnexBTests
{
    // NAL-Kopf: die unteren fuenf Bit sind der Typ.
    private const byte Idr = 0x65;   // 0x65 & 0x1F = 5
    private const byte Sps = 0x67;   // 7
    private const byte Pps = 0x68;   // 8
    private const byte NonIdr = 0x41; // 1

    [Fact]
    public void ErkenntIdrNachVierByteStartcode()
    {
        byte[] frame = [0x00, 0x00, 0x00, 0x01, Idr, 0xAA, 0xBB];
        Assert.True(MediaFoundationH264Encoder.ContainsIdr(frame));
    }

    [Fact]
    public void ErkenntIdrNachDreiByteStartcode()
    {
        byte[] frame = [0x00, 0x00, 0x01, Idr, 0xAA, 0xBB, 0xCC];
        Assert.True(MediaFoundationH264Encoder.ContainsIdr(frame));
    }

    [Fact]
    public void ReineInterFramesSindKeineKeyframes()
    {
        byte[] frame = [0x00, 0x00, 0x00, 0x01, NonIdr, 0x12, 0x34, 0x56];
        Assert.False(MediaFoundationH264Encoder.ContainsIdr(frame));
    }

    [Fact]
    public void FindetIdrAuchHinterAnderenNalUnits()
    {
        // So sieht ein Keyframe aus, dem der Encoder die Parametersaetze
        // bereits selbst vorangestellt hat: SPS, PPS, dann das Bild.
        byte[] frame =
        [
            0x00, 0x00, 0x00, 0x01, Sps, 0x42, 0x00,
            0x00, 0x00, 0x00, 0x01, Pps, 0xCE,
            0x00, 0x00, 0x00, 0x01, Idr, 0x88,
        ];

        Assert.True(MediaFoundationH264Encoder.ContainsIdr(frame));
        Assert.True(MediaFoundationH264Encoder.ContainsParameterSet(frame));
    }

    [Fact]
    public void KeyframeOhneParametersaetzeWirdAlsSolcherErkannt()
    {
        // Genau dieser Fall loest das Voranstellen von SPS/PPS aus.
        byte[] frame = [0x00, 0x00, 0x00, 0x01, Idr, 0x88, 0x99];

        Assert.True(MediaFoundationH264Encoder.ContainsIdr(frame));
        Assert.False(MediaFoundationH264Encoder.ContainsParameterSet(frame));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x01 })]
    public void ZuKurzeDatenWerfenNicht(byte[] frame)
    {
        // Ein abgeschnittenes Paket darf die Encoder-Schleife nicht beenden -
        // sonst steht der Stream still, waehrend die Sitzung "verbunden" anzeigt.
        Assert.False(MediaFoundationH264Encoder.ContainsIdr(frame));
        Assert.False(MediaFoundationH264Encoder.ContainsParameterSet(frame));
    }

    [Fact]
    public void NullBytesOhneStartcodeErzeugenKeinenTreffer()
    {
        byte[] frame = [0x00, 0x00, 0x00, 0x00, 0x00, Idr];
        Assert.False(MediaFoundationH264Encoder.ContainsIdr(frame));
    }
}
