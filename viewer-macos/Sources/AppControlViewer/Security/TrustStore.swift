import Foundation

struct KnownPeer: Codable, Equatable {
    let staticKey: String       // base64
    var name: String
    let fingerprint: String
    let firstSeen: Date
    var lastSeen: Date
    var sessions: Int
}

enum TrustVerdict: Equatable {
    /// Unbekannter Schlüssel und unbekannter Name — normales erstes Pairing.
    case newPeer
    /// Schlüssel bekannt.
    case known(KnownPeer)
    /// Bekannter Name, **anderer** Schlüssel. Rote Warnung in der UI.
    case keyChanged(KnownPeer)
}

/// Trust On First Use für die Viewer-Seite.
///
/// Der Viewer merkt sich die Hosts, mit denen er verbunden war. Ein
/// Schlüsselwechsel bei bekanntem Namen bedeutet: Der Rechner wurde neu
/// aufgesetzt — oder jemand gibt sich als der Freund aus. Beides muss der Nutzer
/// erfahren, bevor er seinen Bildschirminhalt betrachtet und Eingaben schickt.
///
/// Gegenstück: `TrustStore.cs`. Siehe docs/05-security.md §5.5.
final class TrustStore {
    private let fileURL: URL
    private var peers: [KnownPeer] = []
    private let lock = NSLock()

    init(directory: URL? = nil) {
        let base = directory ?? FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("AppControl", isDirectory: true)

        try? FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        self.fileURL = base.appendingPathComponent("peers.json")
        load()
    }

    var allPeers: [KnownPeer] {
        lock.lock(); defer { lock.unlock() }
        return peers
    }

    func evaluate(staticKey: Data, name: String) -> TrustVerdict {
        let encoded = staticKey.base64EncodedString()

        lock.lock(); defer { lock.unlock() }

        if let match = peers.first(where: { $0.staticKey == encoded }) {
            return .known(match)
        }
        if let match = peers.first(where: { $0.name.caseInsensitiveCompare(name) == .orderedSame }) {
            return .keyChanged(match)
        }
        return .newPeer
    }

    /// Nach einer erfolgreich zustande gekommenen Sitzung aufzurufen — **nicht**
    /// nach dem bloßen Handshake. Sonst würde ein fehlgeschlagener
    /// Verbindungsversuch den Schlüssel eines Angreifers als „bekannt" ablegen.
    func remember(staticKey: Data, name: String) {
        let encoded = staticKey.base64EncodedString()
        let now = Date()

        lock.lock(); defer { lock.unlock() }

        if let index = peers.firstIndex(where: { $0.staticKey == encoded }) {
            peers[index].name = name
            peers[index].lastSeen = now
            peers[index].sessions += 1
        } else {
            peers.append(KnownPeer(
                staticKey: encoded,
                name: name,
                fingerprint: Handshake.fingerprint(staticKey),
                firstSeen: now,
                lastSeen: now,
                sessions: 1))
        }
        save()
    }

    func forget(staticKey: String) {
        lock.lock(); defer { lock.unlock() }
        peers.removeAll { $0.staticKey == staticKey }
        save()
    }

    private func load() {
        guard let data = try? Data(contentsOf: fileURL) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // Fail-safe: Bei kaputter Datei mit leerer Liste weitermachen. Die Folge
        // ist eine TOFU-Warnung beim nächsten Verbinden — mehr Vorsicht, nicht
        // weniger.
        peers = (try? decoder.decode([KnownPeer].self, from: data)) ?? []
    }

    private func save() {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(peers) else { return }
        // Atomar schreiben: Ein Absturz mitten im Schreiben darf keine halbe
        // Datei hinterlassen.
        try? data.write(to: fileURL, options: .atomic)
    }
}
