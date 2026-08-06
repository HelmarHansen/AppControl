using System.Text.Json;
using System.Windows;
using AppControl.Host.Audit;
using AppControl.Host.Capture;
using AppControl.Host.Config;
using AppControl.Host.Media;
using AppControl.Host.Input;
using AppControl.Host.Net;
using AppControl.Host.Security;
using AppControl.Host.Ui;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using SIPSorcery.Net;

namespace AppControl.Host.Core;

/// <summary>
/// Verdrahtet alle Komponenten zu einer Sitzung: Signaling, Handshake, Consent,
/// Capture, Encoder, Input.
///
/// Diese Klasse ist die einzige Stelle, an der die Module voneinander wissen -
/// alle anderen kennen nur ihre eigene Aufgabe und die SessionStateMachine. Wer
/// verstehen will, wie AppControl funktioniert, liest diese Datei.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private readonly SessionStateMachine _state;
    private readonly HostSettings _settings;
    private readonly IdentityStore _identity;
    private readonly TrustStore _trust;
    private readonly IAuditLog _audit;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SessionController> _log;

    private readonly SignalingClient _signaling;
    private readonly PeerConnectionManager _peer;
    private readonly InputGuard _guard;
    private readonly InputInjector _injector;
    private readonly InputDispatcher _dispatcher;
    private readonly CaptureEngine _capture;
    private readonly IVideoEncoder _encoder;

    private PairingTicket? _ticket;
    private SecureChannel? _handshakeChannel;
    private SecureChannel? _sessionChannel;
    private Key? _ephemeralKey;
    private byte[]? _ownNonce;
    private byte[]? _peerNonce;
    private HandshakeResult? _handshakeResult;
    private IceServerConfig[] _iceServers = [];

    private System.Threading.Timer? _durationTimer;
    private System.Threading.Timer? _idleTimer;
    private DateTimeOffset _lastInputAt = DateTimeOffset.UtcNow;

    public SessionController(
        SessionStateMachine state, HostSettings settings, IdentityStore identity,
        TrustStore trust, IAuditLog audit, CaptureEngine capture, IVideoEncoder encoder,
        ILoggerFactory loggerFactory)
    {
        _state = state;
        _settings = settings;
        _identity = identity;
        _trust = trust;
        _audit = audit;
        _capture = capture;
        _encoder = encoder;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<SessionController>();

        _signaling = new SignalingClient(loggerFactory.CreateLogger<SignalingClient>());
        _peer = new PeerConnectionManager(loggerFactory.CreateLogger<PeerConnectionManager>());
        _guard = new InputGuard(state, settings);
        _injector = new InputInjector(state, loggerFactory.CreateLogger<InputInjector>());
        _dispatcher = new InputDispatcher(_guard, _injector, state,
            loggerFactory.CreateLogger<InputDispatcher>());

        WireEvents();
    }

    private void WireEvents()
    {
        _signaling.PeerJoined += OnPeerJoined;
        _signaling.PeerLeft += () => _state.Stop(StopReason.PeerDisconnected);
        _signaling.EnvelopeReceived += OnEnvelopeReceived;
        _signaling.IceServersReceived += servers => _iceServers = servers;
        _signaling.Disconnected += _ => _state.Stop(StopReason.ConnectionFailed);

        _peer.InputDataReceived += data =>
        {
            _lastInputAt = DateTimeOffset.UtcNow;
            _dispatcher.HandleRaw(data);
        };
        _peer.ControlMessageReceived += OnControlMessage;
        _peer.ConnectionStateChanged += OnPeerConnectionStateChanged;
        _peer.KeyframeRequested += () => _encoder.RequestKeyframe();
        _peer.BitrateEstimateChanged += bps => _encoder.SetBitrate(
            Math.Min(bps, _settings.MaxBitrateKbps * 1000));

        _dispatcher.EventRejected += (reason, count) =>
            _peer.SendControl(ControlMessages.FromRejection(reason, count));

        // ── DER FRAME-PFAD ───────────────────────────────────────────────────
        // Capture -> Encoder -> Netz. Das Zustands-Gate sitzt bereits IN der
        // CaptureEngine, also vor dem Encoder: Ein pausierter Host verbraucht
        // keine Encoder-Zeit, und es gibt keinen Puffer mit alten Frames.
        _capture.FrameReady += frame =>
        {
            try
            {
                _encoder.EncodeFrame(frame.Surface, frame.SystemRelativeTime);
            }
            finally
            {
                // IMMER freigeben, auch bei Fehler - sonst steht die Pipeline
                // dauerhaft (siehe CaptureEngine.ReleaseFrame).
                _capture.ReleaseFrame();
            }
        };

        _capture.SourceClosed += () =>
        {
            _log.LogInformation("Freigegebenes Fenster wurde geschlossen - Sitzung endet");
            _state.Stop(StopReason.UserStopped);
        };

        _encoder.FrameEncoded += frame => _peer.SendVideo(frame, _encoder.CurrentSettings.FrameRate);

        // Zustandsaenderungen sofort an den Viewer melden. Das ist der Mechanismus,
        // durch den der Viewer eine Pause von einem Netzproblem unterscheiden kann.
        _state.SnapshotChanged += snapshot => _peer.SendControl(ControlMessages.ShareState.From(snapshot));
        _state.StopRequested += OnStopRequested;
    }

    // ── Verbindungsaufbau ────────────────────────────────────────────────────

    public async Task StartPairingAsync(PairingTicket ticket, CancellationToken ct = default)
    {
        _ticket = ticket;
        _state.BeginPairing();

        var handshakeKey = Handshake.DeriveHandshakeKey(ticket.Psk, ticket.RoomId);
        _handshakeChannel = new SecureChannel(
            handshakeKey, ChannelPhase.Handshake, ChannelDirection.HostToViewer);

        _ephemeralKey = Handshake.GenerateEphemeral();
        _ownNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);

        await _signaling.ConnectAsync(new Uri(_settings.SignalingUrl), ticket.RoomIdForSignaling(), ct);
    }

    private void OnPeerJoined()
    {
        _log.LogInformation("Gegenpart im Raum - sende Handshake");
        SendHandshake();
    }

    private void SendHandshake()
    {
        if (_handshakeChannel is null || _ephemeralKey is null || _ownNonce is null) return;

        var message = new HandshakeMessage
        {
            EphemeralPublicKey = Convert.ToBase64String(Handshake.PublicKeyBytes(_ephemeralKey)),
            StaticPublicKey = Convert.ToBase64String(_identity.StaticPublicKey),
            Name = _identity.DeviceName,
            Nonce = Convert.ToBase64String(_ownNonce),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(message, Handshake.JsonOptions);
        _ = _signaling.SendEnvelopeAsync(_handshakeChannel.Seal(json));
    }

    private void OnEnvelopeReceived(byte[] envelope)
    {
        // Zuerst als Session-Umschlag versuchen (der haeufigere Fall nach dem
        // Handshake), dann als Handshake-Umschlag.
        if (_sessionChannel?.Open(envelope) is { } sessionPlain)
        {
            HandleSessionMessage(sessionPlain);
            return;
        }

        if (_handshakeChannel?.Open(envelope) is { } handshakePlain)
        {
            HandleHandshakeMessage(handshakePlain);
            return;
        }

        // Weder noch: falscher PSK, Manipulation oder Replay.
        _log.LogWarning("Umschlag konnte nicht geöffnet werden - verworfen");

        // TODO(Erweiterung): Zaehler mit Schwellwert. Nach >10 Fehlversuchen in
        // 60 s die Verbindung trennen (docs/04-protocol.md §4.8).
    }

    private void HandleHandshakeMessage(byte[] plaintext)
    {
        var message = JsonSerializer.Deserialize<HandshakeMessage>(plaintext, Handshake.JsonOptions);
        if (message is null || _ephemeralKey is null || _ownNonce is null) return;

        _peerNonce = Convert.FromBase64String(message.Nonce);
        var peerEphemeral = Convert.FromBase64String(message.EphemeralPublicKey);
        var peerStatic = Convert.FromBase64String(message.StaticPublicKey);

        _handshakeResult = Handshake.ComputeSession(
            HandshakeRole.Host,
            _ephemeralKey,
            _identity.StaticKey,
            peerEphemeral,
            peerStatic,
            nonceHost: _ownNonce,
            nonceViewer: _peerNonce,
            peerName: message.Name);

        _sessionChannel = new SecureChannel(
            _handshakeResult.SessionKey, ChannelPhase.Session, ChannelDirection.HostToViewer);

        _audit.Write(AuditEventKind.SasComputed, $"sas={_handshakeResult.SasEmoji}");

        var (verdict, known) = _trust.Evaluate(peerStatic, message.Name);
        if (verdict == TrustVerdict.KeyChanged)
            _audit.Write(AuditEventKind.TrustWarning, $"Schlüsselwechsel bei '{message.Name}'");

        _state.PeerConnected(new PeerIdentity(
            Name: message.Name,
            Fingerprint: Handshake.Fingerprint(peerStatic),
            IsKnown: verdict == TrustVerdict.Known,
            PreviousSessions: known?.Sessions ?? 0));

        // Antworten, falls wir zuerst dran waren (der Handshake ist symmetrisch).
        SendHandshake();
    }

    private void HandleSessionMessage(byte[] plaintext)
    {
        using var doc = JsonDocument.Parse(plaintext);
        var root = doc.RootElement;
        if (!root.TryGetProperty("t", out var t)) return;

        switch (t.GetString())
        {
            case "consent-request":
                var wantsControl = root.TryGetProperty("wantsControl", out var wc) && wc.GetBoolean();
                Application.Current.Dispatcher.BeginInvoke(() => ShowConsentDialog(wantsControl));
                break;

            case "sdp":
                var sdp = root.GetProperty("sdp").GetString() ?? "";
                _peer.SetRemoteAnswer(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = sdp,
                });
                break;

            case "ice":
                _peer.AddIceCandidate(new RTCIceCandidateInit
                {
                    candidate = root.GetProperty("candidate").GetString(),
                    sdpMid = root.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                    sdpMLineIndex = root.TryGetProperty("sdpMLineIndex", out var idx)
                        ? (ushort)idx.GetUInt16() : (ushort)0,
                });
                break;

            case "bye":
                _state.Stop(StopReason.PeerDisconnected);
                break;
        }
    }

    // ── Consent ──────────────────────────────────────────────────────────────

    private void ShowConsentDialog(bool viewerWantsControl)
    {
        if (_handshakeResult is null) return;

        var snapshot = _state.Current;
        var peerStatic = _handshakeResult.PeerStaticPublicKey;
        var (verdict, known) = _trust.Evaluate(peerStatic, _handshakeResult.PeerName);

        _audit.Write(AuditEventKind.ConsentRequested,
            $"peer={_handshakeResult.PeerName} wantsControl={viewerWantsControl}");

        var dialog = new ConsentDialog(
            new ConsentRequest(
                ViewerName: _handshakeResult.PeerName,
                Fingerprint: Handshake.Fingerprint(peerStatic),
                SasEmoji: _handshakeResult.SasEmoji,
                Trust: verdict,
                KnownPeer: known,
                ViewerWantsControl: viewerWantsControl),
            _settings);

        dialog.ShowDialog();
        var decision = dialog.Decision;

        // Antwort an den Viewer - auch bei Ablehnung, damit er nicht ins Leere wartet.
        SendSessionMessage(new
        {
            t = "consent-response",
            granted = decision.Granted,
            scope = decision.Scope is null ? null : ControlMessages.ScopeInfo.From(decision.Scope),
            controlGranted = decision.ControlGranted,
            maxDurationSec = decision.MaxDuration == TimeSpan.MaxValue
                ? (long?)null : (long)decision.MaxDuration.TotalSeconds,
        });

        if (!decision.Granted)
        {
            _audit.Write(AuditEventKind.ConsentDenied);
            _state.Stop(StopReason.ConsentDenied);
            return;
        }

        // Erst JETZT, nach der bewussten Zustimmung, wird der Peer als bekannt
        // vermerkt. Ein abgelehnter Verbindungsversuch darf den Schluessel eines
        // Angreifers nicht als "bekannt" ablegen.
        _trust.Remember(peerStatic, _handshakeResult.PeerName);

        _state.ApplyConsent(decision);
        _ = StartMediaAsync(decision);
    }

    private async Task StartMediaAsync(ConsentDecision decision)
    {
        var rect = decision.Scope!.GetTargetRect();

        _encoder.Initialize(new EncoderSettings
        {
            Width = rect.Width,
            Height = rect.Height,
            FrameRate = _settings.MaxFrameRate,
            BitrateBps = _settings.InitialBitrateKbps * 1000,
        });

        if (!_capture.Start(decision.Scope))
        {
            _log.LogError("Erfassung konnte nicht gestartet werden");
            _state.Stop(StopReason.ConnectionFailed);
            return;
        }

        var offer = await _peer.CreateOfferAsync(_iceServers);
        SendSessionMessage(new { t = "sdp", kind = "offer", sdp = offer.sdp });

        StartTimers(decision.MaxDuration);
    }

    private void StartTimers(TimeSpan maxDuration)
    {
        if (maxDuration != TimeSpan.MaxValue)
        {
            _durationTimer = new System.Threading.Timer(
                _ => _state.Stop(StopReason.MaxDurationReached), null, maxDuration, Timeout.InfiniteTimeSpan);
        }

        // Steuerung wird nach Untaetigkeit automatisch entzogen - die Freigabe
        // laeuft weiter. Erneutes Erteilen erfordert einen Klick des Hosts.
        var idleTimeout = TimeSpan.FromMinutes(_settings.ControlIdleTimeoutMinutes);
        _idleTimer = new System.Threading.Timer(_ =>
        {
            if (!_state.Current.ControlGranted) return;
            if (DateTimeOffset.UtcNow - _lastInputAt < idleTimeout) return;

            _state.SetControlGranted(false, "idle-timeout");
            _dispatcher.ReleaseEverything();
            _peer.SendControl(new ControlMessages.ControlState
            {
                Granted = false,
                Reason = "idle-timeout",
            });
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    // ── Steuerung waehrend der Sitzung ───────────────────────────────────────

    public void Pause()
    {
        _state.Pause();
        _capture.Stop();
        _dispatcher.ReleaseEverything();   // gehaltene Tasten sofort freigeben
    }

    public void Resume()
    {
        var scope = _state.Current.Scope;
        if (scope is null) return;
        _state.Resume();
        _capture.Start(scope);
    }

    public void SetControlGranted(bool granted)
    {
        _state.SetControlGranted(granted, granted ? "host-granted" : "host-revoked");
        if (!granted) _dispatcher.ReleaseEverything();
        _peer.SendControl(new ControlMessages.ControlState
        {
            Granted = granted,
            Reason = granted ? "host-granted" : "host-revoked",
        });
    }

    private void OnControlMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "request-control":
                    // Der Viewer kann bitten - entscheiden tut der Host.
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        var answer = MessageBox.Show(
                            $"{_state.Current.Peer?.Name} bittet um Maus- und Tastatursteuerung.\n\n" +
                            "Dein Gegenüber kann dann im freigegebenen Bereich alles tun, was du tun kannst.",
                            "AppControl — Steuerung freigeben?",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No);   // Standard ist NEIN
                        SetControlGranted(answer == MessageBoxResult.Yes);
                    });
                    break;

                case "request-keyframe":
                    _encoder.RequestKeyframe();
                    break;

                case "viewport":
                    var width = root.GetProperty("width").GetInt32();
                    var height = root.GetProperty("height").GetInt32();
                    // TODO(Erweiterung): Entprellen. Ein Aufloesungswechsel baut den
                    // MFT neu auf; beim Ziehen am Fensterrand kaemen sonst Dutzende
                    // Neuaufbauten pro Sekunde. 300 ms Debounce genuegen.
                    _encoder.Reconfigure(width, height, _encoder.CurrentSettings.FrameRate);
                    break;

                case "ping":
                    _peer.SendControl(new
                    {
                        t = "pong",
                        id = root.GetProperty("id").GetInt32(),
                        tsMicros = root.GetProperty("tsMicros").GetInt64(),
                    });
                    break;

                case "bye":
                    _state.Stop(StopReason.PeerDisconnected);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unlesbare Control-Nachricht verworfen");
        }
    }

    private void OnPeerConnectionStateChanged(RTCPeerConnectionState state)
    {
        switch (state)
        {
            case RTCPeerConnectionState.disconnected:
                // Fail-closed: Bei Verbindungsproblemen wird PAUSIERT, nicht
                // weitergestreamt in der Hoffnung, dass es schon gut geht.
                _log.LogWarning("Verbindung unterbrochen - pausiere und warte auf Wiederherstellung");
                if (_state.Current.State == SessionState.Sharing) Pause();
                break;

            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                _state.Stop(StopReason.ConnectionFailed);
                break;

            case RTCPeerConnectionState.connected:
                if (_state.Current.State == SessionState.Paused) Resume();
                break;
        }
    }

    // ── Abbau ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Der Abbau in der Reihenfolge aus docs/03-consent-and-transparency.md §3.6.
    /// Die Reihenfolge ist nicht beliebig: Erst wird alles verboten, dann
    /// abgebaut. Ein Event, das waehrend des Abbaus eintrifft, darf nicht mehr
    /// durchkommen.
    /// </summary>
    private void OnStopRequested(StopReason reason)
    {
        _log.LogInformation("Baue Sitzung ab: {Reason}", reason);

        // 1. + 2. Steuerung ist bereits in SessionStateMachine.Stop() entzogen;
        //          hier werden die gehaltenen Tasten freigegeben.
        _dispatcher.ReleaseEverything();

        // 3. + 4. Keine neuen Frames, keine alten im Puffer.
        _capture.Stop();

        // 5. + 6. Verbindung abbauen - vorher noch ein bye, damit der Viewer
        //         weiss, dass es Absicht war und kein Netzfehler.
        _peer.SendControl(new ControlMessages.Bye { Reason = reason.ToString() });
        _ = _peer.DisposeAsync();

        _durationTimer?.Dispose();
        _idleTimer?.Dispose();
        _durationTimer = null;
        _idleTimer = null;

        _ = _signaling.DisposeAsync();

        _sessionChannel = null;
        _handshakeChannel = null;
        _handshakeResult = null;

        _state.ResetToIdle();
    }

    private void SendSessionMessage(object message)
    {
        if (_sessionChannel is null) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ControlMessages.Options);
        _ = _signaling.SendEnvelopeAsync(_sessionChannel.Seal(json));
    }

    public async ValueTask DisposeAsync()
    {
        _injector.Dispose();
        _capture.Dispose();
        _encoder.Dispose();
        await _peer.DisposeAsync();
        await _signaling.DisposeAsync();
    }
}
