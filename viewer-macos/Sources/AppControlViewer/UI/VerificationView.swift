import SwiftUI

/// SAS-Verifikation: Der Nutzer vergleicht fünf Emojis mit dem, was der Host zeigt.
///
/// Das ist die einzige Stelle, an der ein Man-in-the-Middle auffliegt. Ein
/// Angreifer, der sich zwischen Host und Viewer schaltet, müsste zwei
/// verschiedene Sitzungsschlüssel etablieren — und die Wahrscheinlichkeit, dass
/// beide dieselben fünf Emojis ergeben, liegt bei 1 : 2³⁰.
///
/// Deshalb ist dieser Bildschirm bewusst unumgehbar und die Bestätigung eine
/// eigene, nicht vorausgewählte Handlung. Siehe docs/05-security.md §5.4.
struct VerificationView: View {
    let sas: String
    let hostName: String
    let fingerprint: String
    let trust: TrustSummary
    let onConfirm: () -> Void
    let onCancel: () -> Void

    @State private var confirmed = false

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Sicherheitsprüfung").font(.system(size: 24, weight: .bold))
                Text("Verbunden mit **\(hostName)**")
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 12) {
                Text("VERGLEICHT DIESE SYMBOLE")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(.secondary)

                Text(sas)
                    .font(.system(size: 44))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 18)
                    .background(Color.secondary.opacity(0.12))
                    .clipShape(RoundedRectangle(cornerRadius: 10))

                Text("Auf dem Bildschirm deines Gegenübers stehen dieselben fünf Symbole. "
                   + "Lest sie euch am Telefon vor oder schickt sie euch per Chat.\n\n"
                   + "**Stimmen sie nicht überein, brich ab** — dann hat sich jemand "
                   + "dazwischengeschaltet.")
                    .font(.system(size: 12))
                    .fixedSize(horizontal: false, vertical: true)
            }

            trustBadge

            Toggle("Die Symbole stimmen überein", isOn: $confirmed)
                .toggleStyle(.checkbox)

            HStack {
                Button("Abbrechen", role: .cancel, action: onCancel)
                Spacer()
                Button {
                    onConfirm()
                } label: {
                    Label("Fortfahren", systemImage: "checkmark.shield.fill")
                        .padding(.horizontal, 8)
                }
                .disabled(!confirmed)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(30)
        .frame(width: 520)
    }

    @ViewBuilder
    private var trustBadge: some View {
        if trust.keyChanged {
            // Der Fall, der auffallen muss. Formulierung ohne Beschönigung:
            // Der häufigste Grund ist harmlos, der seltene ist ein Angriff —
            // beide müssen benannt werden, damit die Person selbst entscheiden kann.
            VStack(alignment: .leading, spacing: 6) {
                Label("Der Schlüssel dieses Rechners hat sich geändert",
                      systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 13, weight: .semibold))
                Text("Ihr wart schon \(trust.previousSessions)× verbunden, aber mit einem "
                   + "anderen Schlüssel. Das passiert bei einer Neuinstallation — "
                   + "oder bei einem Angriff. Frag nach, bevor du fortfährst.")
                    .font(.system(size: 11))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.red.opacity(0.15))
            .clipShape(RoundedRectangle(cornerRadius: 8))
        } else if trust.isKnown {
            Label("Bekannter Rechner · \(trust.previousSessions) frühere Sitzungen",
                  systemImage: "checkmark.seal.fill")
                .font(.system(size: 12))
                .foregroundStyle(.green)
        } else {
            Label("Erste Verbindung mit diesem Rechner · Fingerprint \(fingerprint)",
                  systemImage: "person.badge.key")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
        }
    }
}
