using Windows.Graphics;
using Windows.Graphics.DirectX.Direct3D11;

namespace AppControl.Host.Encoding;

/// <summary>Ein kodiertes Videopaket im Annex-B-Format (Startcodes 00 00 00 01).</summary>
public sealed record EncodedFrame(
    ReadOnlyMemory<byte> Data,
    bool IsKeyframe,
    TimeSpan Timestamp,
    TimeSpan Duration);

public sealed record EncoderSettings
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 60;
    public int BitrateBps { get; init; } = 6_000_000;

    /// <summary>
    /// KEIN periodischer Keyframe. Uebliche Streams senden alle 2 s einen I-Frame;
    /// der ist 10-30x groesser als ein P-Frame und erzeugt einen periodischen
    /// Latenz-Spike. Bei einer Punkt-zu-Punkt-Verbindung mit Rueckkanal ist das
    /// unnoetig: Der Viewer meldet Paketverlust per RTCP-PLI, und NUR dann
    /// erzeugen wir einen Keyframe. Siehe docs/02-tech-stack.md §2.1.
    /// </summary>
    public int KeyframeIntervalFrames { get; init; } = 0;

    /// <summary>
    /// B-Frames referenzieren KUENFTIGE Frames - der Encoder muss also mindestens
    /// ein Frame zurueckhalten, bevor er ausgeben kann. Das ist bei Interaktion
    /// direkt spuerbare Zusatzlatenz und deshalb immer aus.
    /// </summary>
    public bool AllowBFrames { get; init; } = false;

    public bool LowLatencyMode { get; init; } = true;
}

/// <summary>
/// Abstraktion ueber den Videoencoder.
///
/// Der Codec ist hinter dieser Schnittstelle austauschbar, ohne die Capture- oder
/// Netzwerkschicht anzufassen. H.264 ist heute die richtige Wahl (Hardware-Encoder
/// auf praktisch jeder Windows-GPU seit ~2012, Hardware-Decoder in jedem Mac seit
/// Ivy Bridge); AV1 wird es in einigen Jahren sein. Siehe docs/02-tech-stack.md §2.1.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Wird pro fertigem Paket ausgeloest - moeglicherweise auf einem Encoder-Thread.</summary>
    event Action<EncodedFrame>? FrameEncoded;

    void Initialize(EncoderSettings settings);

    /// <summary>
    /// Kodiert eine GPU-Oberflaeche. Die Oberflaeche darf nach der Rueckkehr
    /// wiederverwendet werden - der Encoder haelt keine Referenz.
    /// </summary>
    void EncodeFrame(IDirect3DSurface surface, TimeSpan timestamp);

    /// <summary>Naechstes Frame als Keyframe erzwingen. Antwort auf RTCP-PLI.</summary>
    void RequestKeyframe();

    /// <summary>Bitrate zur Laufzeit anpassen - vom WebRTC-Congestion-Control gesteuert.</summary>
    void SetBitrate(int bitrateBps);

    /// <summary>Aufloesung aendern (Viewer hat die Fenstergroesse geaendert).</summary>
    void Reconfigure(int width, int height, int frameRate);

    EncoderSettings CurrentSettings { get; }
}
