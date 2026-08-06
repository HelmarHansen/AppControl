import SwiftUI

struct SettingsView: View {
    @ObservedObject var coordinator: SessionCoordinator

    @State private var signalingUrl: String = ""
    @State private var useEventTap = false
    @State private var requestControlByDefault = false
    @State private var hasAccessibility = InputCapture.hasAccessibilityPermission()

    var body: some View {
        Form {
            Section("Verbindung") {
                TextField("Signaling-Server", text: $signalingUrl)
                    .textFieldStyle(.roundedBorder)
                Text("Muss derselbe Server sein, den auch der Host benutzt.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Eingaben") {
                Toggle("Standardmäßig Steuerung anfragen", isOn: $requestControlByDefault)

                Toggle("Systemweite Tastenkombinationen weiterleiten", isOn: $useEventTap)
                    .disabled(!hasAccessibility)

                // Der Tap ist opt-in und die App funktioniert ohne ihn
                // vollständig — deshalb wird die Berechtigung erklärt statt
                // beim Start eingefordert.
                VStack(alignment: .leading, spacing: 6) {
                    Text("Leitet Cmd+Tab, Cmd+Q und Funktionstasten an den Host weiter, "
                       + "statt sie lokal in macOS zu verarbeiten. Ohne diese Option "
                       + "funktioniert alles andere weiterhin.")
                        .font(.caption).foregroundStyle(.secondary)

                    if !hasAccessibility {
                        HStack(spacing: 8) {
                            Label("Bedienungshilfen-Berechtigung fehlt", systemImage: "lock.fill")
                                .font(.caption).foregroundStyle(.orange)
                            Button("Erteilen …") {
                                InputCapture.requestAccessibilityPermission()
                            }
                            .controlSize(.small)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .onAppear {
            signalingUrl = coordinator.settings.signalingUrl
            useEventTap = coordinator.settings.useSystemEventTap
            requestControlByDefault = coordinator.settings.requestControlByDefault
            hasAccessibility = InputCapture.hasAccessibilityPermission()
        }
        .onChange(of: signalingUrl) { save() }
        .onChange(of: useEventTap) { save() }
        .onChange(of: requestControlByDefault) { save() }
    }

    private func save() {
        var settings = coordinator.settings
        settings.signalingUrl = signalingUrl
        settings.useSystemEventTap = useEventTap
        settings.requestControlByDefault = requestControlByDefault
        settings.save()
        coordinator.settings = settings
    }
}
