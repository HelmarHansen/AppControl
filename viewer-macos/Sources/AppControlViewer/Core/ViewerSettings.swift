import Foundation

/// Einstellungen des Viewers, gespeichert in `UserDefaults`.
struct ViewerSettings: Codable {
    var signalingUrl: String = "wss://localhost:8787/ws"

    /// Systemweiten CGEventTap nutzen, um Cmd+Tab, Cmd+Q & Co. an den Host
    /// durchzureichen. Erfordert die Bedienungshilfen-Berechtigung und ist
    /// deshalb **opt-in** — die App funktioniert auch ohne vollständig.
    var useSystemEventTap: Bool = false

    /// Beim Verbinden gleich Steuerung anfragen. Der Host entscheidet trotzdem
    /// separat; das hier ist nur die Anfrage.
    var requestControlByDefault: Bool = false

    /// Video auf die Fenstergröße skalieren statt in Originalauflösung zeigen.
    var scaleToFit: Bool = true

    private static let key = "AppControl.ViewerSettings"

    static func load() -> ViewerSettings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let settings = try? JSONDecoder().decode(ViewerSettings.self, from: data) else {
            return ViewerSettings()
        }
        return settings
    }

    func save() {
        guard let data = try? JSONEncoder().encode(self) else { return }
        UserDefaults.standard.set(data, forKey: Self.key)
    }
}
