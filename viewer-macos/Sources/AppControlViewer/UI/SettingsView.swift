import SwiftUI

/// Einstellungen des Viewers.
///
/// Die Steuerelemente schreiben über durchreichende `Binding`s direkt in die
/// gespeicherten Einstellungen, statt den Zustand in `@State` zu spiegeln und
/// per `.onChange` nachzuziehen. Zwei Gründe:
///
/// 1. Kein doppelter Zustand, der auseinanderlaufen kann.
/// 2. `.onChange(of:)` mit parameterloser Closure gibt es erst ab macOS 14 —
///    diese Fassung kommt ohne aus und hält damit die in Package.swift
///    deklarierte Untergrenze macOS 13 ein.
struct SettingsView: View {
    @ObservedObject var coordinator: SessionCoordinator

    /// Wird nur gesetzt, um nach dem Erteilen der Berechtigung neu zu prüfen —
    /// das ist echter View-Zustand und gehört deshalb in @State.
    @State private var hasAccessibility = InputCapture.hasAccessibilityPermission()

    var body: some View {
        Form {
            Section("Verbindung") {
                TextField("Signaling-Server", text: binding(\.signalingUrl))
                    .textFieldStyle(.roundedBorder)
                Text("Muss derselbe Server sein, den auch der Host benutzt.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Eingaben") {
                Toggle("Standardmäßig Steuerung anfragen",
                       isOn: binding(\.requestControlByDefault))

                Toggle("Systemweite Tastenkombinationen weiterleiten",
                       isOn: binding(\.useSystemEventTap))
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
                                // Der Systemdialog läuft asynchron; nach kurzer
                                // Verzögerung erneut prüfen, damit der Schalter
                                // ohne Neustart der Einstellungen freigegeben wird.
                                Task {
                                    try? await Task.sleep(for: .seconds(1))
                                    hasAccessibility = InputCapture.hasAccessibilityPermission()
                                }
                            }
                            .controlSize(.small)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 460)
        .onAppear { hasAccessibility = InputCapture.hasAccessibilityPermission() }
    }

    /// Durchreichendes Binding auf ein Feld der Einstellungen: Lesen liefert den
    /// aktuellen Wert, Schreiben speichert sofort.
    private func binding<T>(_ keyPath: WritableKeyPath<ViewerSettings, T>) -> Binding<T> {
        Binding(
            get: { coordinator.settings[keyPath: keyPath] },
            set: { newValue in
                var settings = coordinator.settings
                settings[keyPath: keyPath] = newValue
                settings.save()
                coordinator.settings = settings
            })
    }
}
