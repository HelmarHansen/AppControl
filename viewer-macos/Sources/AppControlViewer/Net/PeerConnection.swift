import Foundation
import WebRTC

/// Verwaltet die WebRTC-PeerConnection auf der Viewer-Seite.
///
/// Der Viewer ist der **antwortende** Teil: Er empfängt ein Offer vom Host,
/// erzeugt eine Answer und empfängt anschließend den Video-Track. Die
/// DataChannels werden vom Host geöffnet, sodass hier nur `didOpen` behandelt
/// wird.
///
/// Siehe docs/01-architecture.md §1.3 zur Kanalaufteilung.
@MainActor
final class PeerConnection: NSObject {

    var onVideoTrack: ((RTCVideoTrack) -> Void)?
    var onControlMessage: ((Data) -> Void)?
    var onConnectionStateChanged: ((RTCPeerConnectionState) -> Void)?
    var onIceCandidate: ((RTCIceCandidate) -> Void)?

    /// Ob die Verbindung direkt (P2P) oder über ein TURN-Relay läuft.
    /// Die UI zeigt das an, damit erkennbar ist, ob Traffic über Dritte geht.
    private(set) var isRelayed = false

    private var factory: RTCPeerConnectionFactory?
    private var connection: RTCPeerConnection?
    private var controlChannel: RTCDataChannel?
    private var inputChannel: RTCDataChannel?
    private var cursorChannel: RTCDataChannel?

    override init() {
        super.init()
        RTCInitializeSSL()
    }

    deinit {
        RTCCleanupSSL()
    }

    func setUp(iceServers: [IceServerConfig]) {
        // Hardware-Codec-Factories: Auf Apple Silicon dekodiert VideoToolbox
        // H.264 in der Media Engine. Der Software-Decoder wäre bei 4K@60
        // chancenlos und würde nebenbei den Lüfter anwerfen.
        factory = RTCPeerConnectionFactory(
            encoderFactory: RTCDefaultVideoEncoderFactory(),
            decoderFactory: RTCDefaultVideoDecoderFactory())

        let configuration = RTCConfiguration()
        configuration.iceServers = iceServers.map {
            RTCIceServer(urlStrings: $0.urls, username: $0.username, credential: $0.credential)
        }
        // UnifiedPlan ist der aktuelle Standard; PlanB ist abgekündigt.
        configuration.sdpSemantics = .unifiedPlan
        // gatherContinually: ICE-Kandidaten auch nach dem initialen Sammeln
        // melden. Wichtig bei Netzwechseln (WLAN → LTE) mitten in der Sitzung.
        configuration.continualGatheringPolicy = .gatherContinually

        let constraints = RTCMediaConstraints(
            mandatoryConstraints: nil,
            optionalConstraints: ["DtlsSrtpKeyAgreement": kRTCMediaConstraintsValueTrue])

        connection = factory?.peerConnection(
            with: configuration, constraints: constraints, delegate: self)
    }

    /// Verarbeitet das Offer des Hosts und erzeugt die Answer.
    func handleOffer(_ sdp: String) async throws -> String {
        guard let connection else { throw PeerError.notInitialized }

        let offer = RTCSessionDescription(type: .offer, sdp: sdp)
        try await connection.setRemoteDescription(offer)

        let constraints = RTCMediaConstraints(
            mandatoryConstraints: [
                "OfferToReceiveVideo": kRTCMediaConstraintsValueTrue,
                "OfferToReceiveAudio": kRTCMediaConstraintsValueFalse,
            ],
            optionalConstraints: nil)

        let answer = try await connection.answer(for: constraints)
        try await connection.setLocalDescription(answer)
        return answer.sdp
    }

    func addIceCandidate(_ candidate: RTCIceCandidate) {
        connection?.add(candidate) { error in
            if let error { NSLog("AppControl: ICE-Kandidat abgelehnt: %@", error.localizedDescription) }
        }
    }

    // ── Senden ───────────────────────────────────────────────────────────────

    /// Sendet ein kodiertes Eingabe-Event über den passenden Kanal.
    ///
    /// Die Kanalwahl trifft der `InputEncoder` (siehe `EncodedInput.channel`):
    /// Mausbewegungen gehen über den unreliablen `ac-cursor`, alles andere über
    /// den reliablen `ac-input`. Das hält den kritischen Pfad frei von
    /// Head-of-Line-Blocking.
    func send(_ input: EncodedInput) {
        let channel = input.channel == .cursor ? cursorChannel : inputChannel
        guard let channel, channel.readyState == .open else { return }
        channel.sendData(RTCDataBuffer(data: input.data, isBinary: true))
    }

    func sendControl<T: Encodable>(_ message: T) {
        guard let controlChannel, controlChannel.readyState == .open,
              let data = try? JSONEncoder().encode(message) else { return }
        controlChannel.sendData(RTCDataBuffer(data: data, isBinary: false))
    }

    func close() {
        controlChannel?.close()
        inputChannel?.close()
        cursorChannel?.close()
        connection?.close()
        connection = nil
        factory = nil
    }

    enum PeerError: Error { case notInitialized }
}

// MARK: - RTCPeerConnectionDelegate

extension PeerConnection: RTCPeerConnectionDelegate {

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection,
                                    didAdd rtpReceiver: RTCRtpReceiver,
                                    streams: [RTCMediaStream]) {
        guard let track = rtpReceiver.track as? RTCVideoTrack else { return }
        Task { @MainActor [weak self] in self?.onVideoTrack?(track) }
    }

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection,
                                    didOpen dataChannel: RTCDataChannel) {
        Task { @MainActor [weak self] in
            guard let self else { return }
            switch dataChannel.label {
            case "ac-control":
                self.controlChannel = dataChannel
                dataChannel.delegate = self
            case "ac-input":
                self.inputChannel = dataChannel
                dataChannel.delegate = self
            case "ac-cursor":
                self.cursorChannel = dataChannel
                dataChannel.delegate = self
            default:
                NSLog("AppControl: unbekannter DataChannel '%@' ignoriert", dataChannel.label)
            }
        }
    }

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection,
                                    didChange newState: RTCPeerConnectionState) {
        Task { @MainActor [weak self] in self?.onConnectionStateChanged?(newState) }
    }

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection,
                                    didGenerate candidate: RTCIceCandidate) {
        Task { @MainActor [weak self] in self?.onIceCandidate?(candidate) }
    }

    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection,
                                    didChange newState: RTCIceConnectionState) {
        guard newState == .connected || newState == .completed else { return }

        // Herausfinden, ob der aktive Pfad ein Relay ist. Der Nutzer soll sehen,
        // ob seine Bildschirminhalte über einen Dritten laufen — auch wenn sie
        // dabei Ende-zu-Ende verschlüsselt bleiben.
        peerConnection.statistics { report in
            let usesRelay = report.statistics.values.contains { stat in
                stat.type == "candidate-pair"
                    && (stat.values["state"] as? String) == "succeeded"
                    && (stat.values["remoteCandidateId"] as? String).map { id in
                        report.statistics[id]?.values["candidateType"] as? String == "relay"
                    } ?? false
            }
            Task { @MainActor [weak self] in self?.isRelayed = usesRelay }
        }
    }

    // Nicht genutzte Delegate-Methoden — der Viewer sendet keine Medien.
    nonisolated func peerConnectionShouldNegotiate(_ peerConnection: RTCPeerConnection) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didRemove stream: RTCMediaStream) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didAdd stream: RTCMediaStream) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didChange stateChanged: RTCSignalingState) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didChange newState: RTCIceGatheringState) {}
    nonisolated func peerConnection(_ peerConnection: RTCPeerConnection, didRemove candidates: [RTCIceCandidate]) {}
}

// MARK: - RTCDataChannelDelegate

extension PeerConnection: RTCDataChannelDelegate {
    nonisolated func dataChannelDidChangeState(_ dataChannel: RTCDataChannel) {}

    nonisolated func dataChannel(_ dataChannel: RTCDataChannel,
                                 didReceiveMessageWith buffer: RTCDataBuffer) {
        guard dataChannel.label == "ac-control" else { return }
        let data = buffer.data
        Task { @MainActor [weak self] in self?.onControlMessage?(data) }
    }
}
