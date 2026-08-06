import Crypto
import Foundation
import WebRTC

/// Zustand der Viewer-Sitzung. Steuert, was die UI zeigt.
enum ViewerState: Equatable {
    case idle
    case connecting
    /// Handshake fertig, SAS liegt vor — der Nutzer soll die Emojis vergleichen.
    case verifying(sas: String, hostName: String, fingerprint: String, trust: TrustSummary)
    /// Wartet darauf, dass der Host im Consent-Dialog entscheidet.
    case awaitingConsent
    case streaming(ControlMessage.ShareState)
    case paused(ControlMessage.ShareState)
    case ended(reason: String)
    case failed(message: String)
}

struct TrustSummary: Equatable {
    let isKnown: Bool
    let keyChanged: Bool
    let previousSessions: Int
    let firstSeen: Date?
}

/// Verdrahtet Signaling, Handshake, WebRTC und Eingabeerfassung zu einer Sitzung.
///
/// Diese Klasse ist das Gegenstück zu `SessionController.cs` auf der Host-Seite.
/// Wer verstehen will, wie der Viewer funktioniert, liest diese Datei.
@MainActor
final class SessionCoordinator: ObservableObject {

    @Published private(set) var state: ViewerState = .idle
    @Published private(set) var videoTrack: RTCVideoTrack?
    @Published private(set) var stats: ControlMessage.Stats?
    @Published private(set) var rttMs: Double = 0
    @Published private(set) var isRelayed = false
    @Published private(set) var controlGranted = false

    /// Zuletzt vom Host abgelehnte Eingabe — die UI erklärt damit, warum nichts
    /// passiert, statt still zu versagen.
    @Published private(set) var lastRejection: ControlMessage.InputRejected?

    let inputCapture = InputCapture()

    private let signaling = SignalingClient()
    private let peer = PeerConnection()
    private let identity = IdentityStore()
    private let trustStore = TrustStore()

    private var ticket: PairingTicket?
    private var handshakeChannel: SecureChannel?
    private var sessionChannel: SecureChannel?
    private var ephemeralKey: Curve25519.KeyAgreement.PrivateKey?
    private var ownNonce: Data?
    private var handshakeResult: HandshakeResult?
    private var iceServers: [IceServerConfig] = []
    private var wantsControl = false
    private var pingId = 0
    private var pendingPings: [Int: Int64] = [:]

    /// @Published, damit die Einstellungen-Ansicht ueber durchreichende Bindings
    /// direkt hierher schreiben kann und die UI die Aenderung sofort sieht.
    @Published var settings = ViewerSettings.load()

    init() {
        wireEvents()
    }

    // ── Öffentliche Steuerung ────────────────────────────────────────────────

    func connect(ticket: PairingTicket, requestControl: Bool) {
        self.ticket = ticket
        self.wantsControl = requestControl
        state = .connecting

        let handshakeKey = Handshake.deriveHandshakeKey(psk: ticket.psk, roomId: ticket.roomId)
        handshakeChannel = SecureChannel(
            key: handshakeKey, phase: .handshake, sendDirection: .viewerToHost)

        ephemeralKey = Curve25519.KeyAgreement.PrivateKey()
        var nonce = Data(count: 16)
        nonce.withUnsafeMutableBytes { _ = SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }
        ownNonce = nonce

        guard let url = URL(string: settings.signalingUrl) else {
            state = .failed(message: "Ungültige Signaling-URL: \(settings.signalingUrl)")
            return
        }

        signaling.connect(to: url, roomId: ticket.roomIdForSignaling)
    }

    /// Der Nutzer hat bestätigt, dass die SAS-Emojis übereinstimmen.
    /// Erst danach wird die Consent-Anfrage an den Host geschickt.
    func confirmSas() {
        guard case .verifying = state, handshakeResult != nil else { return }
        state = .awaitingConsent
        sendSessionMessage([
            "t": "consent-request",
            "wantsControl": wantsControl,
            "viewerName": identity.deviceName,
        ])
    }

    func disconnect() {
        sendSessionMessage(["t": "bye", "reason": "user-stopped"])
        teardown(reason: "Von dir beendet")
    }

    /// Bittet den Host um Steuerung. Der Host muss erneut bestätigen.
    func requestControl() {
        peer.sendControl(ControlMessage.RequestControl())
    }

    func requestKeyframe() {
        peer.sendControl(ControlMessage.RequestKeyframe())
    }

    /// Dem Host die neue Viewport-Größe melden, damit er die Encoder-Auflösung
    /// anpassen kann. Ein 4K-Stream in einem 800px-Fenster ist verschwendete
    /// Bandbreite und verschwendete Encoder-Zeit.
    func reportViewport(width: Int, height: Int, scale: Double) {
        peer.sendControl(ControlMessage.Viewport(width: width, height: height, scale: scale))
    }

    // ── Verdrahtung ──────────────────────────────────────────────────────────

    private func wireEvents() {
        signaling.onPeerJoined = { [weak self] in self?.sendHandshake() }
        signaling.onEnvelope = { [weak self] in self?.handleEnvelope($0) }
        signaling.onIceServers = { [weak self] in self?.iceServers = $0 }
        signaling.onPeerLeft = { [weak self] in
            self?.teardown(reason: "Der Host hat die Verbindung getrennt")
        }
        signaling.onServerError = { [weak self] code, message in
            self?.state = .failed(message: Self.describe(serverError: code, message: message))
        }
        signaling.onDisconnected = { [weak self] error in
            guard let self, case .streaming = self.state else { return }
            self.state = .failed(message: "Verbindung zum Signaling-Server verloren")
        }

        peer.onVideoTrack = { [weak self] track in self?.videoTrack = track }
        peer.onControlMessage = { [weak self] in self?.handleControlMessage($0) }
        peer.onConnectionStateChanged = { [weak self] in self?.handlePeerState($0) }
        peer.onIceCandidate = { [weak self] candidate in
            self?.sendSessionMessage([
                "t": "ice",
                "candidate": candidate.sdp,
                "sdpMid": candidate.sdpMid ?? "0",
                "sdpMLineIndex": candidate.sdpMLineIndex,
            ])
        }

        // Eingaben gehen NUR raus, wenn der Host die Steuerung erteilt hat.
        // Der Host prüft das ohnehin (Gate 2), aber gar nicht erst zu senden
        // spart Bandbreite und macht die Absicht im Code sichtbar.
        inputCapture.onInput = { [weak self] encoded in
            guard let self, self.controlGranted else { return }
            self.peer.send(encoded)
        }
    }

    // ── Handshake ────────────────────────────────────────────────────────────

    private func sendHandshake() {
        guard let handshakeChannel, let ephemeralKey, let ownNonce else { return }

        let message = HandshakeMessage(
            ephemeral: ephemeralKey.publicKey.rawRepresentation.base64EncodedString(),
            static: identity.staticPublicKey.base64EncodedString(),
            name: identity.deviceName,
            nonce: ownNonce.base64EncodedString())

        guard let json = try? JSONEncoder().encode(message),
              let envelope = try? handshakeChannel.seal(json) else { return }

        signaling.sendEnvelope(envelope)
    }

    private func handleEnvelope(_ envelope: Data) {
        // Zuerst als Session-Umschlag versuchen (der häufigere Fall nach dem
        // Handshake), dann als Handshake-Umschlag.
        if let plaintext = sessionChannel?.open(envelope) {
            handleSessionMessage(plaintext)
            return
        }
        if let plaintext = handshakeChannel?.open(envelope) {
            handleHandshakeMessage(plaintext)
            return
        }

        // Weder noch: falscher PSK, Manipulation oder Replay.
        NSLog("AppControl: Umschlag konnte nicht geöffnet werden — verworfen")
    }

    private func handleHandshakeMessage(_ plaintext: Data) {
        guard let message = try? JSONDecoder().decode(HandshakeMessage.self, from: plaintext),
              let ephemeralKey, let ownNonce,
              let peerEphemeral = Data(base64Encoded: message.ephemeral),
              let peerStatic = Data(base64Encoded: message.static),
              let peerNonce = Data(base64Encoded: message.nonce) else {
            state = .failed(message: "Handshake-Nachricht war fehlerhaft")
            return
        }

        do {
            let result = try Handshake.computeSession(
                role: .viewer,
                ownEphemeral: ephemeralKey,
                ownStatic: identity.staticKey,
                peerEphemeralPublic: peerEphemeral,
                peerStaticPublic: peerStatic,
                // Salt-Reihenfolge ist IMMER Host zuerst — hier ist der Host der Peer.
                nonceHost: peerNonce,
                nonceViewer: ownNonce,
                peerName: message.name)

            handshakeResult = result
            sessionChannel = SecureChannel(
                key: result.sessionKey, phase: .session, sendDirection: .viewerToHost)

            let verdict = trustStore.evaluate(staticKey: peerStatic, name: message.name)
            let summary: TrustSummary
            switch verdict {
            case .newPeer:
                summary = TrustSummary(isKnown: false, keyChanged: false,
                                       previousSessions: 0, firstSeen: nil)
            case .known(let peer):
                summary = TrustSummary(isKnown: true, keyChanged: false,
                                       previousSessions: peer.sessions, firstSeen: peer.firstSeen)
            case .keyChanged(let peer):
                summary = TrustSummary(isKnown: false, keyChanged: true,
                                       previousSessions: peer.sessions, firstSeen: peer.firstSeen)
            }

            state = .verifying(
                sas: result.sasEmoji,
                hostName: result.peerName,
                fingerprint: Handshake.fingerprint(peerStatic),
                trust: summary)

        } catch {
            // Fehlgeschlagener Schlüsselaustausch bedeutet fast immer: falscher
            // Code oder manipulierter öffentlicher Schlüssel. Beides ist ein
            // Abbruchgrund, kein Wiederholungsfall.
            state = .failed(message: "Schlüsselaustausch fehlgeschlagen: \(error.localizedDescription)")
        }
    }

    private func handleSessionMessage(_ plaintext: Data) {
        guard let object = try? JSONSerialization.jsonObject(with: plaintext) as? [String: Any],
              let type = object["t"] as? String else { return }

        switch type {
        case "consent-response":
            handleConsentResponse(object)

        case "sdp":
            guard let sdp = object["sdp"] as? String else { return }
            Task { await handleOffer(sdp) }

        case "ice":
            guard let candidateString = object["candidate"] as? String else { return }
            peer.addIceCandidate(RTCIceCandidate(
                sdp: candidateString,
                sdpMLineIndex: Int32(object["sdpMLineIndex"] as? Int ?? 0),
                sdpMid: object["sdpMid"] as? String))

        case "bye":
            teardown(reason: Self.describe(byeReason: object["reason"] as? String ?? ""))

        default:
            break
        }
    }

    private func handleConsentResponse(_ object: [String: Any]) {
        let granted = object["granted"] as? Bool ?? false

        guard granted else {
            state = .ended(reason: "Der Host hat die Anfrage abgelehnt")
            return
        }

        controlGranted = object["controlGranted"] as? Bool ?? false
        inputCapture.controlEnabled = controlGranted

        // Erst JETZT, nach einer tatsächlich zustande gekommenen Sitzung, wird
        // der Host als bekannt vermerkt.
        if let result = handshakeResult {
            trustStore.remember(staticKey: result.peerStaticPublicKey, name: result.peerName)
        }

        peer.setUp(iceServers: iceServers)
        startPingLoop()
    }

    private func handleOffer(_ sdp: String) async {
        do {
            let answer = try await peer.handleOffer(sdp)
            sendSessionMessage(["t": "sdp", "kind": "answer", "sdp": answer])
        } catch {
            state = .failed(message: "Medienaushandlung fehlgeschlagen: \(error.localizedDescription)")
        }
    }

    // ── Laufende Sitzung ─────────────────────────────────────────────────────

    private func handleControlMessage(_ data: Data) {
        guard let message = IncomingControl.decode(data) else { return }

        switch message {
        case .shareState(let shareState):
            // Der Zustand kommt AUSSCHLIESSLICH von hier — nie aus dem
            // Videostrom abgeleitet. Ein stehendes Bild wegen Netzproblemen sieht
            // sonst identisch aus wie eine bewusste Pause des Hosts, und der
            // Unterschied ist genau das, worum es bei Transparenz geht.
            controlGranted = shareState.controlGranted
            inputCapture.controlEnabled = shareState.controlGranted

            if shareState.isPaused {
                // Bei Pause sofort alle gehaltenen Tasten freigeben — sonst
                // bliebe eine Taste auf dem Host hängen.
                inputCapture.releaseAll()
                state = .paused(shareState)
            } else if shareState.isSharing {
                state = .streaming(shareState)
            } else {
                teardown(reason: "Der Host hat die Freigabe beendet")
            }

        case .controlState(let controlState):
            controlGranted = controlState.granted
            inputCapture.controlEnabled = controlState.granted
            if !controlState.granted { inputCapture.releaseAll() }

        case .inputRejected(let rejection):
            lastRejection = rejection

        case .cursor:
            // TODO(Erweiterung): Cursorform übernehmen, damit der lokale Zeiger
            // dem entspricht, was auf dem Host unter der Maus liegt (I-Beam über
            // Textfeldern, Hand über Links). Reines Komfort-Feature.
            break

        case .stats(let incoming):
            stats = incoming

        case .pong(let id, _):
            if let sentAt = pendingPings.removeValue(forKey: id) {
                let nowMicros = Int64(Date().timeIntervalSince1970 * 1_000_000)
                rttMs = Double(nowMicros - sentAt) / 1000.0
            }

        case .bye(let reason):
            teardown(reason: Self.describe(byeReason: reason))
        }
    }

    private func handlePeerState(_ newState: RTCPeerConnectionState) {
        isRelayed = peer.isRelayed

        switch newState {
        case .connected:
            inputCapture.start()
        case .failed, .closed:
            teardown(reason: "Die Verbindung ist abgebrochen")
        case .disconnected:
            // Nicht sofort abbauen: `disconnected` ist bei WebRTC oft
            // vorübergehend (kurzer Netzwechsel) und geht in `connected` zurück.
            // Erst `failed` ist endgültig.
            inputCapture.releaseAll()
        default:
            break
        }
    }

    /// Latenzmessung im Sekundentakt. Der angezeigte RTT ist die Zahl, an der
    /// der Nutzer merkt, ob Steuerung gerade sinnvoll ist.
    private func startPingLoop() {
        Task { @MainActor [weak self] in
            while let self, self.signaling.isConnected {
                try? await Task.sleep(for: .seconds(1))
                guard case .streaming = self.state else { continue }

                self.pingId += 1
                let micros = Int64(Date().timeIntervalSince1970 * 1_000_000)
                self.pendingPings[self.pingId] = micros
                self.peer.sendControl(ControlMessage.Ping(id: self.pingId, tsMicros: micros))

                // Alte, unbeantwortete Pings verwerfen, damit das Dictionary
                // nicht unbegrenzt wächst.
                if self.pendingPings.count > 10 {
                    let cutoff = self.pingId - 10
                    self.pendingPings = self.pendingPings.filter { $0.key > cutoff }
                }
            }
        }
    }

    // ── Abbau ────────────────────────────────────────────────────────────────

    private func teardown(reason: String) {
        inputCapture.stop()
        inputCapture.controlEnabled = false
        controlGranted = false
        peer.close()
        signaling.disconnect()

        videoTrack = nil
        sessionChannel = nil
        handshakeChannel = nil
        handshakeResult = nil
        pendingPings.removeAll()

        state = .ended(reason: reason)
    }

    private func sendSessionMessage(_ object: [String: Any]) {
        guard let sessionChannel,
              let json = try? JSONSerialization.data(withJSONObject: object),
              let envelope = try? sessionChannel.seal(json) else { return }
        signaling.sendEnvelope(envelope)
    }

    // ── Fehlermeldungen ──────────────────────────────────────────────────────

    /// Übersetzt Servercodes in Sätze, mit denen ein Nutzer etwas anfangen kann.
    private static func describe(serverError code: String, message: String) -> String {
        switch code {
        case "role-taken":
            return "Es ist bereits ein Viewer mit diesem Code verbunden."
        case "room-full":
            return "Dieser Einladungscode wird bereits von zwei Geräten benutzt."
        case "unsupported-version":
            return "Host und Viewer sprechen unterschiedliche Protokollversionen. "
                 + "Aktualisiere beide Seiten auf denselben Stand."
        case "rate-limited":
            return "Zu viele Anfragen — kurz warten und erneut versuchen."
        default:
            return message.isEmpty ? "Serverfehler: \(code)" : message
        }
    }

    private static func describe(byeReason reason: String) -> String {
        switch reason {
        case "UserStopped":         return "Der Host hat die Freigabe beendet"
        case "EmergencyHotkey":     return "Der Host hat den Notfall-Hotkey benutzt"
        case "MaxDurationReached":  return "Die vereinbarte Sitzungsdauer ist abgelaufen"
        case "ScreenLocked":        return "Der Host hat seinen Bildschirm gesperrt"
        case "ConsentDenied":       return "Der Host hat die Anfrage abgelehnt"
        case "ConsentTimeout":      return "Der Host hat nicht rechtzeitig geantwortet"
        case "OverlayUnavailable":  return "Der Host konnte die Statusanzeige nicht darstellen "
                                         + "— die Freigabe wurde aus Sicherheitsgründen beendet"
        case "PeerDisconnected":    return "Die Verbindung wurde getrennt"
        default:                    return "Die Sitzung wurde beendet (\(reason))"
        }
    }
}
