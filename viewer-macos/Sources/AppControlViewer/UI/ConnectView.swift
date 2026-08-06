import SwiftUI

/// Startbildschirm: Einladungscode eingeben und verbinden.
struct ConnectView: View {
    @ObservedObject var coordinator: SessionCoordinator

    @State private var ticketText = ""
    @State private var requestControl = false
    @State private var parseError: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 6) {
                Text("AppControl").font(.system(size: 30, weight: .bold))
                Text("Verbinde dich mit dem Bildschirm deines Gegenübers.")
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("EINLADUNGSCODE")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.secondary)

                TextEditor(text: $ticketText)
                    .font(.system(size: 12, design: .monospaced))
                    .frame(height: 76)
                    .padding(6)
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(.quaternary))

                if let parseError {
                    Label(parseError, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 11))
                        .foregroundStyle(.orange)
                }

                Text("Der Code kommt vom Host — über Signal, iMessage oder Telefon, "
                   + "nicht über den Signaling-Server.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
            }

            Toggle(isOn: $requestControl) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Steuerung anfragen")
                    Text("Der Host entscheidet trotzdem selbst — das hier ist nur die Anfrage.")
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }
            }

            HStack {
                Spacer()
                Button {
                    connect()
                } label: {
                    Label("Verbinden", systemImage: "arrow.right.circle.fill")
                        .padding(.horizontal, 8)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(ticketText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }

            if case .connecting = coordinator.state {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Verbinde mit dem Signaling-Server …").foregroundStyle(.secondary)
                }
            }

            if case .failed(let message) = coordinator.state {
                Label(message, systemImage: "xmark.octagon.fill")
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if case .ended(let reason) = coordinator.state {
                Label(reason, systemImage: "info.circle")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(30)
        .frame(width: 520)
    }

    private func connect() {
        guard let ticket = PairingTicket.parse(ticketText) else {
            // Die Prüfsumme im Ticket macht genau diese Unterscheidung möglich:
            // Ein Tippfehler ist als solcher erkennbar und wird nicht als
            // "Handshake fehlgeschlagen" gemeldet — eine Meldung, die dem Nutzer
            // nichts sagt und leicht mit einem Angriff verwechselt wird.
            parseError = "Der Code ist unvollständig oder enthält einen Tippfehler. "
                       + "Prüfe, ob du ihn komplett kopiert hast."
            return
        }
        parseError = nil
        coordinator.connect(ticket: ticket, requestControl: requestControl)
    }
}
