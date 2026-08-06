import SwiftUI

@main
struct AppControlViewerApp: App {
    @StateObject private var coordinator = SessionCoordinator()
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        // Die Emoji-Tabelle ist Teil des Protokolls und in drei Implementierungen
        // dupliziert. Ein Tippfehler darin würde sich sonst erst beim Nutzer als
        // "die Symbole stimmen nicht überein" äußern — dem am schwersten zu
        // diagnostizierenden Fehlerbild, das dieses Protokoll hat.
        EmojiTable.validate()
    }

    var body: some Scene {
        WindowGroup {
            RootView(coordinator: coordinator)
                .onDisappear { coordinator.disconnect() }
        }
        .windowResizability(.contentSize)
        .commands {
            CommandGroup(replacing: .newItem) { }   // "Neu" ergibt hier keinen Sinn

            CommandMenu("Sitzung") {
                Button("Verbindung beenden") { coordinator.disconnect() }
                    .keyboardShortcut(".", modifiers: .command)
                Divider()
                Button("Bild neu aufbauen") { coordinator.requestKeyframe() }
                    .keyboardShortcut("r", modifiers: .command)
                Button("Steuerung anfragen") { coordinator.requestControl() }
            }
        }

        Settings {
            SettingsView(coordinator: coordinator)
        }
    }
}

/// Fensterebene: Fokusverlust muss beim Host gehaltene Tasten freigeben.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidResignActive(_ notification: Notification) {
        // Ohne diese Meldung bliebe eine Taste auf dem Host hängen, wenn der
        // Nutzer mitten im Tastendruck zu einer anderen macOS-App wechselt —
        // ein Zustand, der dort nur durch manuelles Drücken auflösbar wäre.
        NotificationCenter.default.post(name: .appControlShouldReleaseKeys, object: nil)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}

extension Notification.Name {
    static let appControlShouldReleaseKeys = Notification.Name("AppControl.shouldReleaseKeys")
}

/// Wählt anhand des Sitzungszustands den passenden Bildschirm.
struct RootView: View {
    @ObservedObject var coordinator: SessionCoordinator

    var body: some View {
        Group {
            switch coordinator.state {
            case .idle, .connecting, .failed, .ended:
                ConnectView(coordinator: coordinator)

            case .verifying(let sas, let hostName, let fingerprint, let trust):
                VerificationView(
                    sas: sas, hostName: hostName, fingerprint: fingerprint, trust: trust,
                    onConfirm: { coordinator.confirmSas() },
                    onCancel: { coordinator.disconnect() })

            case .awaitingConsent:
                AwaitingConsentView(onCancel: { coordinator.disconnect() })

            case .streaming, .paused:
                SessionView(coordinator: coordinator)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .appControlShouldReleaseKeys)) { _ in
            coordinator.inputCapture.releaseAll()
        }
    }
}

/// Wartebildschirm, während der Host entscheidet.
///
/// Er benennt ausdrücklich, dass der Host gerade gefragt wird — damit klar ist,
/// dass hier nichts hängt, sondern jemand eine bewusste Entscheidung trifft.
struct AwaitingConsentView: View {
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 18) {
            ProgressView().controlSize(.large)

            Text("Warte auf die Freigabe")
                .font(.title2.weight(.semibold))

            Text("Dein Gegenüber entscheidet gerade, was freigegeben wird und ob du "
               + "steuern darfst. Ohne diese Zustimmung wird nichts übertragen.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Button("Abbrechen", role: .cancel, action: onCancel)
        }
        .padding(40)
        .frame(width: 440)
    }
}
