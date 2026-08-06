import Foundation

/// Nachrichten des DataChannels `ac-control`. Siehe docs/04-protocol.md §4.4.
///
/// Die Host→Viewer-Nachrichten sind die **einzige** Quelle für das, was der Viewer
/// anzeigt. Der Viewer leitet nichts aus dem Videostrom ab: Ein stehendes Bild
/// wegen Netzproblemen sieht sonst identisch aus wie eine bewusste Pause des
/// Hosts — und der Unterschied ist genau das, worum es bei Transparenz geht.
enum ControlMessage {

    // ── Host → Viewer ────────────────────────────────────────────────────────

    struct ScopeInfo: Codable, Equatable {
        let kind: String        // "window" | "screen"
        let title: String
        let width: Int
        let height: Int

        var isWindow: Bool { kind == "window" }
    }

    struct ShareState: Codable, Equatable {
        let state: String       // "sharing" | "paused" | "stopped"
        let scope: ScopeInfo?
        let controlGranted: Bool
        let elapsedSec: Int
        let remainingSec: Int?

        var isSharing: Bool { state == "sharing" }
        var isPaused: Bool { state == "paused" }
    }

    struct ControlState: Codable, Equatable {
        let granted: Bool
        let reason: String

        /// Für den Nutzer verständliche Erklärung, warum die Steuerung weg ist.
        var explanation: String {
            switch reason {
            case "host-revoked":        return "Der Host hat die Steuerung beendet."
            case "idle-timeout":        return "Die Steuerung wurde nach längerer Inaktivität automatisch beendet."
            case "window-not-focused":  return "Das freigegebene Fenster ist nicht im Vordergrund."
            case "host-paused":         return "Der Host hat die Freigabe pausiert."
            case "host-granted":        return "Der Host hat dir die Steuerung erlaubt."
            default:                    return reason
            }
        }
    }

    /// Ein Event wurde vom Host verworfen. Damit kann der Viewer erklären, warum
    /// nichts passiert, statt still zu versagen.
    struct InputRejected: Codable, Equatable {
        let gate: Int
        let reason: String
        let count: Int

        var explanation: String {
            switch reason {
            case "session-not-sharing":
                return "Der Host teilt gerade nicht."
            case "control-not-granted":
                return "Du hast keine Steuerungsfreigabe."
            case "target-window-not-foreground":
                return "Das freigegebene Fenster ist beim Host nicht im Vordergrund — "
                     + "deine Eingaben werden absichtlich nicht weitergeleitet."
            case "target-rect-invalid":
                return "Das freigegebene Fenster ist gerade nicht sichtbar."
            case "key-blocked-by-policy":
                return "Diese Taste ist bei App-Freigabe gesperrt, weil sie den "
                     + "freigegebenen Bereich verlassen würde."
            default:
                return reason
            }
        }
    }

    struct CursorShape: Codable, Equatable {
        let shape: String
        let visible: Bool
    }

    struct Stats: Codable, Equatable {
        let fps: Double
        let bitrateKbps: Int
        let rttMs: Double
        let encodeMs: Double
        let packetsLost: Int
    }

    struct Bye: Codable, Equatable {
        let reason: String
    }

    // ── Viewer → Host ────────────────────────────────────────────────────────

    struct RequestControl: Encodable { let t = "request-control" }
    struct RequestKeyframe: Encodable { let t = "request-keyframe" }

    struct Viewport: Encodable {
        let t = "viewport"
        let width: Int
        let height: Int
        let scale: Double
    }

    struct Ping: Encodable {
        let t = "ping"
        let id: Int
        let tsMicros: Int64
    }

    struct Pong: Encodable {
        let t = "pong"
        let id: Int
        let tsMicros: Int64
    }
}

/// Ergebnis des Dekodierens einer Control-Nachricht.
enum IncomingControl {
    case shareState(ControlMessage.ShareState)
    case controlState(ControlMessage.ControlState)
    case inputRejected(ControlMessage.InputRejected)
    case cursor(ControlMessage.CursorShape)
    case stats(ControlMessage.Stats)
    case pong(id: Int, tsMicros: Int64)
    case bye(reason: String)

    /// Dekodiert eine JSON-Nachricht.
    ///
    /// Gibt `nil` bei unbekanntem Typ zurück, statt zu werfen — Vorwärts-
    /// kompatibilität: Ein neuerer Host darf einen älteren Viewer nicht zum
    /// Trennen bringen (docs/04-protocol.md §4.8).
    static func decode(_ data: Data) -> IncomingControl? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["t"] as? String else { return nil }

        let decoder = JSONDecoder()

        switch type {
        case "share-state":
            return (try? decoder.decode(ControlMessage.ShareState.self, from: data)).map(Self.shareState)
        case "control-state":
            return (try? decoder.decode(ControlMessage.ControlState.self, from: data)).map(Self.controlState)
        case "input-rejected":
            return (try? decoder.decode(ControlMessage.InputRejected.self, from: data)).map(Self.inputRejected)
        case "cursor":
            return (try? decoder.decode(ControlMessage.CursorShape.self, from: data)).map(Self.cursor)
        case "stats":
            return (try? decoder.decode(ControlMessage.Stats.self, from: data)).map(Self.stats)
        case "pong":
            guard let id = object["id"] as? Int, let ts = object["tsMicros"] as? Int64 else { return nil }
            return .pong(id: id, tsMicros: ts)
        case "bye":
            return .bye(reason: object["reason"] as? String ?? "unknown")
        default:
            return nil
        }
    }
}
