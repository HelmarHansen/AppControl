# 6 — Setup, Build und Test

Reihenfolge: **Signaling-Server zuerst** (beide Clients brauchen ihn), dann Host,
dann Viewer.

---

## 6.0 Was in diesem Repository lauffähig ist — ehrlich

| Komponente | Zustand |
|---|---|
| `signaling/` | **Vollständig lauffähig.** `npm test` → 27 Tests grün. |
| `tools/crypto-vectors/` | **Vollständig lauffähig.** `node verify.mjs` → 10 Prüfungen grün. |
| `tools/consistency/` | **Vollständig lauffähig.** `python3 check.py` → 52 Prüfungen grün. |
| `host-windows/` | **Gerüst mit markierten Lücken.** Zustandsmaschine, Gates, Krypto, UI und Protokoll sind vollständig. Drei Stellen sind als Implementierungsleitfaden ausgeführt statt fertig: `D3D11Helper.CreateDevice()`, `MediaFoundationH264Encoder` und `WindowThumbnailProvider`. Bis die erste steht, startet die Anwendung nicht. |
| `viewer-macos/` | **Gerüst mit einer Lücke.** Alles außer der Cursorform-Übernahme ist ausgeführt; die Abhängigkeit `stasel/WebRTC` muss beim ersten Build geladen werden. |

Die Lücken sind bewusst dort, wo der Code reine Interop-Mechanik ist (Media
Foundation, D3D11) — nicht in der Logik, die AppControl ausmacht. Jede trägt einen
schrittweisen Leitfaden im Kommentar. Siehe [`07-roadmap.md`](07-roadmap.md) für
die Reihenfolge und den geschätzten Aufwand.

---

## 6.1 Signaling-Server

### Lokal (zum Ausprobieren)

```bash
cd signaling
npm install
npm run build
npm start                      # lauscht auf http://localhost:8787
```

Prüfen:

```bash
curl http://localhost:8787/health
# {"ok":true,"service":"appcontrol-signaling","protocol":"ac/1","rooms":0,...}
```

Tests:

```bash
npm test                       # 27 Tests: Raumverwaltung, Protokoll, End-to-End
```

### Konfiguration

Alles über Umgebungsvariablen, keine Konfigurationsdatei:

| Variable | Standard | Zweck |
|---|---|---|
| `PORT` | `8787` | Port |
| `HOST` | `0.0.0.0` | Bind-Adresse |
| `LOG_LEVEL` | `info` | `debug` \| `info` \| `warn` \| `error` |
| `MAX_PAYLOAD_CHARS` | `131072` | Obergrenze pro Relay-Nachricht |
| `RATE_LIMIT_PER_SEC` | `30` | Nachrichten/s pro Verbindung |
| `ROOM_TTL_MS` | `600000` | Leere Räume verfallen nach 10 min |
| `STUN_URLS` | Google-STUN | Kommagetrennt |
| `TURN_URLS` | — | Kommagetrennt, aktiviert TURN |
| `TURN_SECRET` | — | Gemeinsames Geheimnis mit coturn |

### Deployment mit TLS

`wss://` ist **Pflicht** — nicht als Ersatz für die Ende-zu-Ende-Verschlüsselung
(die läuft darunter unabhängig), sondern damit ein Mitleser nicht sieht, wer wann
mit welcher Raum-ID verbindet.

**Fly.io** (schnellster Weg, TLS automatisch):

```bash
cd signaling
fly launch --name mein-appcontrol-signal --no-deploy
fly deploy
# → wss://mein-appcontrol-signal.fly.dev/ws
```

**Eigener VPS mit Caddy** (Caddy holt das Zertifikat selbst):

```bash
# /etc/caddy/Caddyfile
signal.example.com {
    reverse_proxy localhost:8787
}
```

```bash
sudo tee /etc/systemd/system/appcontrol-signal.service <<'EOF'
[Unit]
Description=AppControl Signaling
After=network.target

[Service]
Type=simple
User=appcontrol
WorkingDirectory=/opt/appcontrol/signaling
ExecStart=/usr/bin/node dist/index.js
Environment=PORT=8787
Environment=LOG_LEVEL=info
Restart=always

# Der Server braucht nichts vom System außer einem Socket.
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl enable --now appcontrol-signal
```

### TURN (optional, für die ~15 % der Verbindungen ohne P2P-Pfad)

```bash
sudo apt install coturn
```

```conf
# /etc/turnserver.conf
listening-port=3478
tls-listening-port=5349
fingerprint
use-auth-secret
static-auth-secret=<gleiches Geheimnis wie TURN_SECRET>
realm=example.com
total-quota=100
no-multicast-peers
# Kein stdout-Logging von Sitzungsdaten
no-stdout-log
```

Dann im Signaling-Server:

```bash
TURN_URLS=turn:turn.example.com:3478,turns:turn.example.com:5349
TURN_SECRET=<dasselbe Geheimnis>
```

Der Server stellt daraus **kurzlebige** Zugangsdaten aus (10 min gültig), sodass in
der ausgelieferten App kein Passwort steht.

---

## 6.2 Windows-Host

### Voraussetzungen

| | |
|---|---|
| Windows | 10 Version 1803 (Build 17134) oder neuer — Untergrenze für `Windows.Graphics.Capture` |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| optional | Visual Studio 2022 mit „.NET-Desktopentwicklung" |

Keine Adminrechte, kein Treiber, keine Signierung.

### Bauen

```powershell
cd host-windows
dotnet restore
dotnet build -c Release

# Einzelne EXE ohne installierte .NET-Runtime beim Empfänger:
dotnet publish src/AppControl.Host/AppControl.Host.csproj `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -o publish
```

Ergebnis: `publish/AppControl.exe` (~70 MB self-contained). Weitergeben genügt —
kein Installer, kein Registry-Eintrag, kein Autostart.

### Konfigurieren

`appsettings.json` neben der EXE:

```json
{
  "signalingUrl": "wss://signal.example.com/ws",
  "defaultMaxSessionMinutes": 30,
  "controlIdleTimeoutMinutes": 5,
  "consentTimeoutSeconds": 60
}
```

### Tests

```powershell
dotnet test
```

Prüft Krypto gegen die gemeinsamen Vektoren, die Input-Gates, den Binärdecoder und
die HID-Abbildung. Falls `vectors.json` fehlt:

```bash
node tools/crypto-vectors/generate.mjs > tools/crypto-vectors/vectors.json
```

### Erste Schritte

1. `AppControl.exe` starten → Dashboard und Tray-Icon erscheinen
2. „Neue Einladung erzeugen" → Code kopieren
3. Code über Signal/iMessage/Telefon an den Viewer schicken — **nicht** über den Signaling-Server
4. „Auf Verbindung warten" klicken
5. Wenn der Viewer verbindet: SAS-Emojis vergleichen, Freigabe wählen, zustimmen

---

## 6.3 macOS-Viewer

### Voraussetzungen

| | |
|---|---|
| macOS | 13 Ventura oder neuer |
| Xcode | 15 oder neuer (bzw. Command Line Tools) |
| Swift | 5.9+ |

### Bauen (SwiftPM)

```bash
cd viewer-macos
swift build -c release
.build/release/AppControlViewer
```

Der erste Build lädt `WebRTC.xcframework` (~250 MB) — das dauert. Danach ist es
im Cache.

### Bauen (Xcode, empfohlen für die App-Bundle-Variante)

```bash
cd viewer-macos
open Package.swift          # Xcode öffnet das Paket direkt
# Schema "AppControlViewer" wählen, ⌘R
```

Für ein verteilbares `.app` mit Info.plist und Icon:

```bash
# Xcode → File → New → Project → macOS App
# Dann das SwiftPM-Paket als lokale Abhängigkeit einbinden.
# Der Umweg über ein App-Projekt ist nötig, weil SwiftPM allein keine
# App-Bundles erzeugt — und ohne Bundle gibt es keine Info.plist,
# also auch keinen Erklärungstext für die Bedienungshilfen-Berechtigung.
```

### Berechtigungen

| Berechtigung | Wann nötig | Ohne sie |
|---|---|---|
| **Keine** | Grundbetrieb | funktioniert vollständig |
| **Bedienungshilfen** | für Cmd+Tab, Cmd+Q, F-Tasten an den Host | alles andere funktioniert weiter |

Die Bedienungshilfen-Berechtigung ist bewusst **opt-in** und wird nicht beim Start
eingefordert: Systemeinstellungen → Datenschutz & Sicherheit → Bedienungshilfen.

### Tests

```bash
swift test
```

Die Testvektoren liegen als Symlink auf `tools/crypto-vectors/vectors.json` — eine
Quelle für alle drei Implementierungen.

---

## 6.4 Projektweite Prüfungen

```bash
# Krypto-Referenz gegen sich selbst
node tools/crypto-vectors/verify.mjs

# Die drei Implementierungen gegeneinander
python3 tools/consistency/check.py

# Signaling-Server
cd signaling && npm test
```

Der Konsistenz-Check ist der ungewöhnlichste der drei und der nützlichste beim
Weiterentwickeln: Er vergleicht Emoji-Tabellen, Nachrichtentyp-Bytes,
Header-Layouts, HKDF-Zeichenketten und HID-Abbildungen zwischen C#, Swift und
JavaScript — und prüft zusätzlich die Transparenz-Zusicherungen aus Kapitel 3.
Ein Commit, der den Consent-Default umdreht oder den gelben Rahmen abschaltet,
wird rot.

---

## 6.5 Ende-zu-Ende-Test mit zwei Rechnern

```
┌──────────────┐    ┌─────────────┐    ┌──────────────┐
│ Windows-PC   │───▶│  Signaling  │◀───│ Mac          │
│ AppControl   │    │  :8787      │    │ AppControl   │
│ .exe         │    └─────────────┘    │ Viewer       │
└──────────────┘                        └──────────────┘
```

1. Signaling-Server starten, in beiden Clients dieselbe URL eintragen
2. Host: Einladung erzeugen, Code auf anderem Weg zum Mac bringen
3. Host: „Auf Verbindung warten"
4. Viewer: Code einfügen, „Verbinden"
5. **Beide Seiten zeigen jetzt fünf Emojis** — vergleichen
6. Viewer bestätigt → Host sieht den Consent-Dialog
7. Host wählt Freigabe und Steuerung → Stream startet

Zum Testen auf **einem** Rechner: Host in einer Windows-VM (Parallels/UTM),
Viewer auf dem Mac-Host, Signaling lokal. Die VM braucht GPU-Beschleunigung,
sonst fällt WGC auf sehr niedrige Frameraten zurück.

---

## 6.6 Manuelle Testfälle

Diese Fälle prüfen die Zusicherungen aus Kapitel 3. Bis Integrationstests
existieren (siehe [`07-roadmap.md`](07-roadmap.md)), sind sie von Hand
abzuarbeiten — sie sind der eigentliche Abnahmetest des Projekts.

### Consent

| # | Vorgehen | Erwartet |
|---|---|---|
| C1 | Im Consent-Dialog Enter drücken | **Ablehnung**, keine Freigabe |
| C2 | Escape drücken | Ablehnung |
| C3 | Fenster über X schließen | Ablehnung |
| C4 | 60 s nichts tun | Automatische Ablehnung, Viewer bekommt Meldung |
| C5 | Ohne Scope-Auswahl „Freigeben" anklicken | Button ist deaktiviert |
| C6 | Freigeben ohne Steuerungsauswahl | Sitzung startet **ohne** Steuerung |

### Gate 3 — der wichtigste Test

| # | Vorgehen | Erwartet |
|---|---|---|
| G1 | Notepad freigeben mit Steuerung, Viewer bewegt Maus | Funktioniert |
| G2 | Host klickt in ein **anderes** Fenster | Viewer-Eingaben laufen ins Leere, Viewer sieht „Zielfenster nicht im Vordergrund" |
| G3 | Host klickt zurück in Notepad | Eingaben funktionieren wieder |
| G4 | Viewer sendet Koordinate weit außerhalb | Klick landet am Fensterrand, nicht in einer anderen App |
| G5 | Viewer drückt Cmd (→ Windows-Taste) | Startmenü öffnet sich **nicht**, Zähler steigt |

### Not-Aus

| # | Vorgehen | Erwartet |
|---|---|---|
| E1 | Strg+Alt+Umschalt+X während Steuerung aktiv | Sofortiger Stopp, Overlay weg, Tray grau |
| E2 | Rechte Strg dreimal schnell | Dito |
| E3 | Viewer hält Umschalt, Host drückt Not-Aus | **Umschalt ist danach nicht mehr gedrückt** |
| E4 | Viewer sendet Strg+Alt+Umschalt+X | Passiert nichts — der Hotkey bleibt dem Host vorbehalten |
| E5 | Overlay-Button „Beenden" | Sofortiger Stopp |
| E6 | Bildschirm sperren (Win+L) | Sitzung endet automatisch |

### Overlay

| # | Vorgehen | Erwartet |
|---|---|---|
| O1 | Vollbild-Anwendung starten | Overlay bleibt sichtbar (spätestens nach 2 s wieder oben) |
| O2 | Overlay über Task-Manager beenden | Wird wiederhergestellt; nach dem dritten Fehlversuch endet die Sitzung |
| O3 | Alt+Tab drücken | Overlay taucht nicht in der Liste auf |
| O4 | Freigegebenes Fenster wechseln | Overlay-Text ändert sich sofort |

### Transparenz

| # | Vorgehen | Erwartet |
|---|---|---|
| T1 | Notepad per WGC freigeben | Windows zeichnet einen **gelben Rahmen** darum |
| T2 | Pause drücken | Viewer sieht Standbild **mit Banner** „Der Host hat pausiert" |
| T3 | Netzwerkkabel ziehen | Viewer sieht „Verbindungsproblem" — **nicht** dasselbe wie Pause |
| T4 | Steuerung entziehen | Viewer-Statusleiste wechselt sofort auf „Nur Ansicht" |

### Krypto

| # | Vorgehen | Erwartet |
|---|---|---|
| K1 | Ein Zeichen im Ticket ändern | „Code enthält einen Tippfehler" — nicht „Handshake fehlgeschlagen" |
| K2 | Falsches, aber gültig geprüfsummtes Ticket | Handshake schlägt fehl, keine Sitzung |
| K3 | Peer neu installieren, erneut verbinden | Host zeigt **rote Warnung** „Schlüssel geändert" |
| K4 | Zweite Sitzung mit demselben Peer | Host zeigt „bekannt seit …", Consent-Dialog erscheint **trotzdem** |

---

## 6.7 Fehlerbehebung

| Symptom | Ursache | Lösung |
|---|---|---|
| Host: „Windows.Graphics.Capture wird nicht unterstützt" | Windows älter als 1803 | Aktualisieren, oder Desktop-Duplication-Fallback implementieren (siehe Roadmap) |
| Host startet nicht, `NotImplementedException` in `D3D11Helper` | Der Platzhalter ist noch nicht ausgefüllt | Siehe Leitfaden in `Capture/D3D11Helper.cs` |
| Verbindung bleibt bei „Verbinde …" | Signaling-URL falsch oder Server nicht erreichbar | `curl https://<server>/health` |
| Beide verbunden, aber kein Bild | ICE findet keinen Pfad | TURN konfigurieren; im Viewer prüfen, ob „Über Relay" steht |
| SAS-Emojis unterschiedlich | **Möglicher MitM** — oder unterschiedliche Protokollversionen | **Nicht fortfahren.** Erst `python3 tools/consistency/check.py` laufen lassen |
| Eingaben kommen nicht an | Gate 3: freigegebenes Fenster nicht im Vordergrund | Host muss das Fenster aktivieren |
| Eingaben kommen in Admin-Fenstern nicht an | Windows-UIPI-Grenze | Beabsichtigt und nicht umgehbar — der Host muss selbst handeln |
| Viewer: Cmd+Tab erreicht den Host nicht | Bedienungshilfen fehlen | Einstellungen → Eingaben → „Erteilen …" |
| Ruckeln, hohe Latenz | Bandbreite oder GPU | Auflösung senken; im Viewer die fps/Mbit/s-Anzeige beobachten |
