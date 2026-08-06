using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
// VideoFormat und VideoCodecsEnum leben ab SIPSorcery 10 in
// SIPSorceryMedia.Abstractions, nicht mehr in SIPSorcery.Net.
using SIPSorceryMedia.Abstractions;
using AppControl.Host.Media;

namespace AppControl.Host.Net;

/// <summary>
/// Verwaltet die WebRTC-PeerConnection: einen Video-Track und drei DataChannels.
///
/// DIE KANALAUFTEILUNG IST DER WICHTIGSTE LATENZ-TRICK DES PROTOKOLLS:
///
///   ac-control  reliable, ordered      Zustand, Consent, Statistik
///   ac-input    reliable, ordered      Tasten, Klicks, Scroll
///   ac-cursor   UNRELIABLE, unordered  reine Mausbewegungen
///
/// Bei Paketverlust blockiert ein Retransmit auf einem reliable-ordered Channel
/// ALLE nachfolgenden Nachrichten (Head-of-Line-Blocking). Mausbewegungen sind mit
/// Abstand die haeufigsten Events - und zugleich die, bei denen ein verlorenes
/// Paket voellig egal ist, weil das naechste es ohnehin ueberholt. Sie ueber einen
/// unreliablen Kanal zu schicken haelt den kritischen Pfad frei.
///
/// Umgekehrt MUESSEN Tastendruecke zuverlaessig sein: Ein verlorenes "Taste
/// losgelassen" hinterlaesst eine haengende Taste auf dem Host.
///
/// Siehe docs/01-architecture.md §1.3.
/// </summary>
public sealed class PeerConnectionManager : IAsyncDisposable
{
    private readonly ILogger<PeerConnectionManager> _log;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _control;
    private RTCDataChannel? _input;
    private RTCDataChannel? _cursor;
    private MediaStreamTrack? _videoTrack;

    private uint _rtpTimestamp;

    // CS0067 ist hier bewusst unterdrueckt: KeyframeRequested und
    // BitrateEstimateChanged gehoeren zum Vertrag dieser Klasse und werden vom
    // SessionController bereits abonniert. Ausgeloest werden sie erst, wenn die
    // RTCP-Auswertung in OnReceiveReport ausimplementiert ist (dort als
    // PLATZHALTER markiert). Die Events jetzt zu entfernen wuerde den
    // Verdrahtungscode im Controller unnoetig aendern und spaeter zurueckbauen.
#pragma warning disable CS0067
    public event Action<string>? ControlMessageReceived;
    public event Action<byte[]>? InputDataReceived;
    public event Action<RTCPeerConnectionState>? ConnectionStateChanged;
    public event Action? KeyframeRequested;
    public event Action<int>? BitrateEstimateChanged;
#pragma warning restore CS0067

    public PeerConnectionManager(ILogger<PeerConnectionManager> log) => _log = log;

    public RTCPeerConnectionState State => _pc?.connectionState ?? RTCPeerConnectionState.closed;

    public async Task<RTCSessionDescriptionInit> CreateOfferAsync(IceServerConfig[] iceServers)
    {
        var config = new RTCConfiguration
        {
            iceServers = [.. iceServers.Select(s => new RTCIceServer
            {
                urls = string.Join(',', s.Urls),
                username = s.Username,
                credential = s.Credential,
            })],
        };

        _pc = new RTCPeerConnection(config);

        // ── Video-Track ──────────────────────────────────────────────────────
        // H.264 im Passthrough: SIPSorcery packetisiert nur, kodiert nicht.
        // Unser Media-Foundation-Encoder liefert bereits fertige NAL-Units.
        _videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, payloadID: 102),
            MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(_videoTrack);

        // ── DataChannels ─────────────────────────────────────────────────────
        _control = await _pc.createDataChannel("ac-control", new RTCDataChannelInit
        {
            ordered = true,   // reliable + ordered ist der Standard
        });

        _input = await _pc.createDataChannel("ac-input", new RTCDataChannelInit
        {
            ordered = true,
        });

        _cursor = await _pc.createDataChannel("ac-cursor", new RTCDataChannelInit
        {
            ordered = false,
            maxRetransmits = 0,   // fire-and-forget: das naechste Event ueberholt jedes verlorene
        });

        _control.onmessage += (_, _, data) => ControlMessageReceived?.Invoke(Encoding.UTF8.GetString(data));
        _input.onmessage += (_, _, data) => InputDataReceived?.Invoke(data);
        _cursor.onmessage += (_, _, data) => InputDataReceived?.Invoke(data);

        _pc.onconnectionstatechange += state =>
        {
            _log.LogInformation("PeerConnection-Zustand: {State}", state);
            ConnectionStateChanged?.Invoke(state);
        };

        _pc.OnReceiveReport += (_, _, report) =>
        {
            // RTCP-PLI (Picture Loss Indication) = der Viewer hat Frames verloren
            // und kann ohne Keyframe nicht weiter dekodieren. Das ist der EINZIGE
            // Grund, aus dem wir einen Keyframe erzeugen - siehe EncoderSettings.
            if (report.Bye is not null) return;
            if (report.SenderReport is not null || report.ReceiverReport is not null)
            {
                // PLATZHALTER: Auswertung von Paketverlust und Jitter, um daraus
                // eine Bitratenempfehlung abzuleiten und BitrateEstimateChanged
                // auszuloesen.
            }
        };

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        return offer;
    }

    public void SetRemoteAnswer(RTCSessionDescriptionInit answer)
    {
        var result = _pc?.setRemoteDescription(answer);
        if (result != SetDescriptionResultEnum.OK)
            _log.LogError("Remote-Description abgelehnt: {Result}", result);
    }

    public void AddIceCandidate(RTCIceCandidateInit candidate) => _pc?.addIceCandidate(candidate);

    /// <summary>Sendet ein kodiertes Frame an den Viewer.</summary>
    public void SendVideo(EncodedFrame frame, int frameRate)
    {
        if (_pc is null || _pc.connectionState != RTCPeerConnectionState.connected) return;

        // 90 kHz ist die feste RTP-Zeitbasis fuer Video (RFC 3551).
        var durationUnits = (uint)(90_000 / Math.Max(1, frameRate));
        _rtpTimestamp += durationUnits;

        _pc.SendVideo(durationUnits, frame.Data.ToArray());
    }

    // ── DataChannel-Versand ──────────────────────────────────────────────────

    public void SendControl<T>(T message)
    {
        if (_control is not { readyState: RTCDataChannelState.open }) return;
        try
        {
            _control.send(JsonSerializer.Serialize(message, ControlMessages.Options));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Control-Nachricht konnte nicht gesendet werden");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Reihenfolge zaehlt: erst die Kanaele, dann die Verbindung. Umgekehrt
        // wuerden die close-Handler auf eine bereits abgebaute PC zugreifen.
        _control?.close();
        _input?.close();
        _cursor?.close();
        _pc?.close();
        _pc?.Dispose();
        await ValueTask.CompletedTask;
    }
}
