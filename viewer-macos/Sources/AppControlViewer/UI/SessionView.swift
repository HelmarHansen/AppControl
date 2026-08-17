import SwiftUI
import WebRTC

/// Das Sitzungsfenster: Videobild plus eine Statusleiste, die jederzeit zeigt,
/// was gerade gilt.
///
/// Der Viewer spiegelt bewusst die Transparenz der Host-Seite: Ob gerade
/// gesteuert werden darf, ob pausiert ist und warum eine Eingabe nicht
/// ankommt — all das steht sichtbar da, statt sich als „reagiert nicht" zu
/// äußern.
struct SessionView: View {
    @ObservedObject var coordinator: SessionCoordinator
    @State private var showRejectionBanner = false

    var body: some View {
        VStack(spacing: 0) {
            statusBar

            ZStack {
                VideoRenderView(
                    track: coordinator.videoTrack,
                    cursorShape: coordinator.cursorShape,
                    cursorVisible: coordinator.cursorVisible
                ) { frame in
                    coordinator.inputCapture.videoFrame = frame
                }
                .background(Color.black)

                if case .paused = coordinator.state { pausedOverlay }
                if coordinator.videoTrack == nil { waitingOverlay }
            }

            if let rejection = coordinator.lastRejection, showRejectionBanner {
                rejectionBanner(rejection)
            }

            bottomBar
        }
        .frame(minWidth: 800, minHeight: 500)
        // Einparametrige Closure statt der neueren, parameterlosen Form:
        // .onChange(of:) ohne Parameter gibt es erst ab macOS 14, und
        // Package.swift deklariert macOS 13 als Untergrenze.
        .onChange(of: coordinator.lastRejection?.count) { _ in
            // Banner kurz zeigen und wieder ausblenden — eine Dauereinblendung
            // bei gehaltener Maus wäre nur noch Lärm.
            withAnimation { showRejectionBanner = true }
            Task {
                try? await Task.sleep(for: .seconds(4))
                withAnimation { showRejectionBanner = false }
            }
        }
    }

    // ── Statusleiste ─────────────────────────────────────────────────────────

    private var statusBar: some View {
        HStack(spacing: 14) {
            Circle()
                .fill(statusColor)
                .frame(width: 10, height: 10)

            Text(statusTitle)
                .font(.system(size: 13, weight: .semibold))

            if let scope = currentShareState?.scope {
                Divider().frame(height: 16)
                Image(systemName: scope.isWindow ? "macwindow" : "display")
                Text(scope.title)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .foregroundStyle(.secondary)
            }

            Divider().frame(height: 16)

            // Die wichtigste Anzeige: Darf ich gerade steuern?
            Label(
                coordinator.controlGranted ? "Steuerung erlaubt" : "Nur Ansicht",
                systemImage: coordinator.controlGranted ? "hand.tap.fill" : "eye")
                .font(.system(size: 12, weight: .medium))
                .padding(.horizontal, 8).padding(.vertical, 3)
                .background(coordinator.controlGranted ? Color.orange.opacity(0.25)
                                                       : Color.secondary.opacity(0.15))
                .clipShape(Capsule())

            Spacer()

            // Verbindungsweg: Der Nutzer soll sehen, ob Traffic über einen
            // Dritten läuft — auch wenn er dabei Ende-zu-Ende verschlüsselt ist.
            Label(coordinator.isRelayed ? "Über Relay" : "Direkt (P2P)",
                  systemImage: coordinator.isRelayed ? "arrow.triangle.branch" : "arrow.left.arrow.right")
                .font(.system(size: 11))
                .foregroundStyle(coordinator.isRelayed ? .orange : .secondary)

            if let stats = coordinator.stats {
                Text("\(Int(stats.fps)) fps · \(stats.bitrateKbps / 1000) Mbit/s · \(Int(coordinator.rttMs)) ms")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .background(.regularMaterial)
    }

    // ── Overlays ─────────────────────────────────────────────────────────────

    private var pausedOverlay: some View {
        ZStack {
            Color.black.opacity(0.65)
            VStack(spacing: 14) {
                Image(systemName: "pause.circle.fill")
                    .font(.system(size: 56))
                    .foregroundStyle(.orange)
                Text("Der Host hat pausiert")
                    .font(.title2.weight(.semibold))
                // Diese Zeile ist der Grund, warum der Zustand über den
                // Control-Channel kommt und nicht aus dem Videostrom abgeleitet
                // wird: Sie unterscheidet eine bewusste Pause von einem Netzproblem.
                Text("Du siehst ein Standbild. Deine Eingaben werden nicht übertragen.")
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var waitingOverlay: some View {
        VStack(spacing: 14) {
            ProgressView()
            Text("Warte auf das Videobild …").foregroundStyle(.secondary)
        }
    }

    private func rejectionBanner(_ rejection: ControlMessage.InputRejected) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "hand.raised.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 2) {
                Text("Eingabe wurde vom Host nicht angenommen")
                    .font(.system(size: 12, weight: .semibold))
                Text(rejection.explanation)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            if rejection.count > 1 {
                Text("\(rejection.count)×")
                    .font(.system(size: 11, design: .monospaced))
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(Color.orange.opacity(0.15))
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }

    // ── Untere Leiste ────────────────────────────────────────────────────────

    private var bottomBar: some View {
        HStack(spacing: 12) {
            if !coordinator.controlGranted {
                Button {
                    coordinator.requestControl()
                } label: {
                    Label("Steuerung anfragen", systemImage: "hand.raised")
                }
                .help("Der Host muss die Anfrage bestätigen.")
            }

            Button {
                coordinator.requestKeyframe()
            } label: {
                Label("Bild neu aufbauen", systemImage: "arrow.clockwise")
            }
            .help("Fordert ein vollständiges Einzelbild an — hilft bei Bildfehlern.")

            Spacer()

            if let remaining = currentShareState?.remainingSec {
                Text("Endet in \(formatDuration(remaining))")
                    .font(.system(size: 11))
                    .foregroundStyle(remaining < 120 ? .orange : .secondary)
            }

            Button(role: .destructive) {
                coordinator.disconnect()
            } label: {
                Label("Verbindung beenden", systemImage: "xmark.circle")
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 9)
        .background(.regularMaterial)
    }

    // ── Ableitungen ──────────────────────────────────────────────────────────

    private var currentShareState: ControlMessage.ShareState? {
        switch coordinator.state {
        case .streaming(let s), .paused(let s): return s
        default: return nil
        }
    }

    private var statusColor: Color {
        switch coordinator.state {
        case .streaming: return .red
        case .paused:    return .orange
        case .ended, .failed: return .secondary
        default:         return .yellow
        }
    }

    private var statusTitle: String {
        switch coordinator.state {
        case .idle:            return "Nicht verbunden"
        case .connecting:      return "Verbinde …"
        case .verifying:       return "Sicherheitsprüfung"
        case .awaitingConsent: return "Warte auf Freigabe durch den Host"
        case .streaming:       return "Live"
        case .paused:          return "Pausiert"
        case .ended(let r):    return r
        case .failed(let m):   return m
        }
    }

    private func formatDuration(_ seconds: Int) -> String {
        seconds >= 60 ? "\(seconds / 60) min" : "\(seconds) s"
    }
}
