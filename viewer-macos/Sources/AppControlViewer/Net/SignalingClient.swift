import Foundation

struct IceServerConfig: Codable {
    let urls: [String]
    let username: String?
    let credential: String?
}

/// WebSocket-Client zum Rendezvous-Server.
///
/// Der Server sieht ausschließlich die Raum-ID und undurchsichtige
/// Base64-Blöcke. Diese Klasse kennt daher keinerlei Anwendungslogik — sie
/// transportiert Bytes. Die Verschlüsselung passiert eine Schicht darüber im
/// `SessionCoordinator`. Siehe docs/04-protocol.md §4.1.
@MainActor
final class SignalingClient: NSObject {

    /// Ein verschlüsselter Umschlag von der Gegenseite (bereits Base64-dekodiert).
    var onEnvelope: ((Data) -> Void)?
    var onIceServers: (([IceServerConfig]) -> Void)?
    var onPeerJoined: (() -> Void)?
    var onPeerLeft: (() -> Void)?
    var onServerError: ((String, String) -> Void)?
    var onDisconnected: ((Error?) -> Void)?

    private var task: URLSessionWebSocketTask?
    private var session: URLSession?
    private(set) var isConnected = false

    func connect(to url: URL, roomId: String) {
        // delegateQueue: .main, damit alle Callbacks auf dem Main-Actor landen
        // und die UI ohne zusätzliches Dispatching aktualisiert werden kann.
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 15
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: .main)

        task = session?.webSocketTask(with: url)
        task?.resume()
        isConnected = true

        send(json: ["t": "join", "roomId": roomId, "role": "viewer", "v": "ac/1"])
        receiveNext()
        startKeepAlive()
    }

    /// Sendet einen verschlüsselten Umschlag an die Gegenseite.
    func sendEnvelope(_ envelope: Data) {
        send(json: ["t": "relay", "payload": envelope.base64EncodedString()])
    }

    func disconnect() {
        send(json: ["t": "leave"])
        task?.cancel(with: .normalClosure, reason: nil)
        task = nil
        session?.invalidateAndCancel()
        session = nil
        isConnected = false
    }

    // ── Intern ───────────────────────────────────────────────────────────────

    private func send(json object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object),
              let text = String(data: data, encoding: .utf8) else { return }

        task?.send(.string(text)) { [weak self] error in
            guard let error else { return }
            Task { @MainActor in
                self?.isConnected = false
                self?.onDisconnected?(error)
            }
        }
    }

    private func receiveNext() {
        task?.receive { [weak self] result in
            Task { @MainActor in
                guard let self else { return }

                switch result {
                case .success(let message):
                    switch message {
                    case .string(let text):
                        self.handle(text)
                    case .data(let data):
                        // Der Server sendet ausschließlich Text. Binärframes sind
                        // ein Protokollfehler — verwerfen statt raten.
                        NSLog("AppControl: unerwarteter Binärframe (%d Byte) verworfen", data.count)
                    @unknown default:
                        break
                    }
                    self.receiveNext()   // nächste Nachricht anfordern

                case .failure(let error):
                    self.isConnected = false
                    self.onDisconnected?(error)
                }
            }
        }
    }

    private func handle(_ text: String) {
        guard let data = text.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["t"] as? String else { return }

        switch type {
        case "relay":
            if let payload = object["payload"] as? String,
               let raw = Data(base64Encoded: payload) {
                onEnvelope?(raw)
            }

        case "joined":
            if let peerPresent = object["peerPresent"] as? Bool, peerPresent {
                onPeerJoined?()
            }

        case "peer-joined":
            onPeerJoined?()

        case "peer-left":
            onPeerLeft?()

        case "ice-servers":
            if let servers = object["servers"],
               let serverData = try? JSONSerialization.data(withJSONObject: servers),
               let parsed = try? JSONDecoder().decode([IceServerConfig].self, from: serverData) {
                onIceServers?(parsed)
            }

        case "error":
            onServerError?(
                object["code"] as? String ?? "unknown",
                object["message"] as? String ?? "")

        default:
            break   // Vorwärtskompatibilität: unbekannte Typen ignorieren
        }
    }

    /// WebSocket-Ping alle 20 s. Ohne ihn schließen NAT-Router und Load-Balancer
    /// eine still daliegende Verbindung nach typischerweise 30–60 s.
    private func startKeepAlive() {
        Task { @MainActor [weak self] in
            while let self, self.isConnected {
                try? await Task.sleep(for: .seconds(20))
                guard self.isConnected else { break }
                self.task?.sendPing { _ in }
            }
        }
    }
}

extension SignalingClient: URLSessionWebSocketDelegate {
    nonisolated func urlSession(_ session: URLSession,
                                webSocketTask: URLSessionWebSocketTask,
                                didCloseWith closeCode: URLSessionWebSocketTask.CloseCode,
                                reason: Data?) {
        Task { @MainActor [weak self] in
            self?.isConnected = false
            self?.onDisconnected?(nil)
        }
    }
}
