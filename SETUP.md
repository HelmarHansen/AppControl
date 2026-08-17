# Einrichtung auf beiden Rechnern

Praktische Schritt-für-Schritt-Anleitung. Ausführliche Hintergründe:
[`docs/06-build-and-run.md`](docs/06-build-and-run.md).

---

## Aktueller Stand — was heute geht

| Schritt | Rechner | Zustand |
|---|---|---|
| 1. Signaling-Server | Cloud / VPS | ✅ **läuft sofort** |
| 2. Mac-Viewer bauen | Mac | ✅ **baut und startet sofort** |
| 3. Windows-Host bauen | Windows | ⚠️ **kompiliert, startet aber nicht** — zwei Stellen fehlen |
| 4. Erste Verbindung | beide | ⛔ **blockiert durch Schritt 3** |

Der Host wirft beim Start `NotImplementedException` in `D3D11Helper.CreateDevice()`.
Das ist kein Fehler, sondern eine bewusst offene Stelle mit Implementierungsleitfaden
im Quelltext — siehe [`docs/07-roadmap.md` §7.1](docs/07-roadmap.md).

**Schritt 1 und 2 kannst du trotzdem jetzt schon machen.** Sie sind unabhängig und
danach abgehakt.

---

## Schritt 1 — Signaling-Server (10 Minuten)

Beide Clients brauchen ihn, und er muss **von beiden Rechnern aus über das Internet
erreichbar** sein. `localhost` funktioniert nur, wenn Host und Viewer derselbe
Rechner sind.

### Variante A: Fly.io (empfohlen, kostenlos für diesen Zweck)

```bash
# Auf deinem Mac, im geklonten Repository:
brew install flyctl          # falls noch nicht vorhanden
fly auth signup              # oder: fly auth login

cd signaling
fly launch --name appcontrol-signal-DEINNAME --copy-config --no-deploy
fly deploy
```

Danach prüfen:

```bash
curl https://appcontrol-signal-DEINNAME.fly.dev/health
# {"ok":true,"service":"appcontrol-signaling","protocol":"ac/1","rooms":0,...}
```

**Deine URL für beide Clients:**
`wss://appcontrol-signal-DEINNAME.fly.dev/ws`

Die Maschine fährt bei Nichtbenutzung herunter (`min_machines_running = 0`) und
wird beim ersten Verbindungsversuch in ~2 s geweckt. Für ein Werkzeug, das ein
paarmal pro Woche läuft, bedeutet das praktisch keine Kosten.

### Variante B: Eigener VPS

```bash
# Auf dem Server:
git clone <dein-repo> && cd AppControl/signaling
npm ci && npm run build

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
Restart=always
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl enable --now appcontrol-signal
```

Davor Caddy oder nginx für TLS — `wss://` ist Pflicht. Details in
[`docs/06-build-and-run.md` §6.1](docs/06-build-and-run.md).

### Variante C: Nur zum Ausprobieren, ohne Deployment

Auf dem Mac laufen lassen und per Cloudflare-Tunnel nach außen geben:

```bash
cd signaling && npm ci && npm run build && npm start &
brew install cloudflared
cloudflared tunnel --url http://localhost:8787
# gibt eine https://…trycloudflare.com-URL aus
# → wss://…trycloudflare.com/ws
```

Die URL wechselt bei jedem Start — nur zum Testen brauchbar, nicht dauerhaft.

> **Wichtig:** Der Server sieht nur Ciphertext. Er kann Bildschirminhalte und
> Eingaben nicht mitlesen, selbst wenn er kompromittiert wäre. Deshalb ist es
> vertretbar, ihn bei einem Hoster zu betreiben. Siehe
> [`docs/05-security.md` §5.8](docs/05-security.md).

---

## Schritt 2 — Mac-Viewer (20 Minuten, davon 15 Warten)

### Voraussetzungen

```bash
sw_vers                      # ProductVersion muss >= 13.0 sein
xcode-select --install       # falls noch keine Command Line Tools da sind
swift --version              # >= 5.9
```

### Bauen

```bash
cd viewer-macos
swift test          # übersetzt den Code und prüft Krypto, Protokoll, Tastenabbildung
```

Der erste Lauf lädt `WebRTC.xcframework` (~250 MB) — das dauert 10–15 Minuten
und passiert genau einmal.

> **`swift build -c release` schlägt derzeit beim Linken fehl.** Der Swift-Code
> übersetzt vollständig, aber das Linken der ausführbaren Datei findet
> `RTCMTLNSVideoView` aus dem WebRTC-Framework nicht. Der dokumentierte Weg,
> `stasel/WebRTC` zu nutzen, ist ein **Xcode-App-Projekt**, das das Framework
> einbettet und signiert — und das brauchst du für ein `.app` mit Icon und
> Info.plist ohnehin. Anleitung:
> [`Packaging/README.md`](viewer-macos/Packaging/README.md).

### Tests

```bash
swift test
```

Prüft die Krypto gegen dieselben Vektoren wie der Windows-Host, dazu Binärencoder
und Tastenabbildung.

### Starten

```bash
.build/release/AppControlViewer
```

Beim ersten Start die Signaling-URL eintragen: **AppControl Viewer → Einstellungen
(⌘,) → Verbindung** → deine `wss://…/ws`-Adresse.

### Berechtigungen

Für den Normalbetrieb: **keine**.

Nur wenn du später Cmd+Tab, Cmd+Q und F-Tasten an Windows durchreichen willst,
brauchst du **Bedienungshilfen** (Systemeinstellungen → Datenschutz & Sicherheit →
Bedienungshilfen). Das ist opt-in und wird nicht beim Start eingefordert — alles
andere funktioniert ohne.

### App-Bundle statt Terminal-Start

`swift build` erzeugt eine ausführbare Datei, kein `.app`. Für ein Icon im Dock
und die Info.plist (die den Erklärungstext für die Bedienungshilfen enthält)
brauchst du ein Xcode-App-Projekt, das dieses SwiftPM-Paket einbindet — Anleitung
in [`viewer-macos/Packaging/README.md`](viewer-macos/Packaging/README.md). Zum
Ausprobieren reicht der Terminal-Start.

---

## Schritt 3 — Windows-Host

### Voraussetzungen

| | |
|---|---|
| Windows | 10 Version 1803 (Build 17134) oder neuer — `winver` prüft das |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |

Keine Adminrechte, kein Treiber, kein Installer.

### Bauen

```powershell
cd host-windows
dotnet restore
dotnet build -c Release
dotnet test                    # Krypto, Input-Gates, Decoder, Tastenabbildung
```

Das kompiliert und die Tests laufen durch.

### Starten

```powershell
dotnet run --project src/AppControl.Host
```

Das Tray-Icon erscheint, das Statusfenster zeigt „Bereit". Beim ersten Start
erzeugt der Host seinen Langzeitschlüssel und legt ihn per DPAPI ab.

**Alle markierten Lücken sind geschlossen** — Direct3D-Geräteerstellung,
H.264-Encoder, Live-Vorschau im App-Picker und Zeigerform-Übernahme sind
ausgeführt. Am Code fehlt nichts mehr.

### Beim allerersten Lauf: worauf du achten solltest

Der Encoder ist vollständig geschrieben, aber noch nie auf echter Hardware
gelaufen — eine CI hat weder GPU noch Bildschirm zum Erfassen. Falls die
Verbindung steht und das Bild trotzdem schwarz bleibt, sagt das Log, woran es
liegt:

```powershell
dotnet run --project src/AppControl.Host 2>&1 | Select-String "Encoder|ProcessOutput|D3D"
```

Drei Zeilen sind dabei aussagekräftig:

| Logzeile | Bedeutung |
|---|---|
| `Encoder initialisiert: … asynchron (Hardware)` | Der Normalfall. Hardware-Encoder gefunden. |
| `… synchron (Software)` | Kein Hardware-Encoder. Läuft, kostet aber deutlich mehr CPU. |
| `arbeitet ohne D3D-Device-Manager` | Die Bilder gehen über den Systemspeicher statt über die GPU — etwa dreifache CPU-Last bei 1080p60. |
| `Encoder.ProcessOutput fehlgeschlagen (0x…)` | Der eigentliche Fehlerfall. HRESULT notieren, `docs/07-roadmap.md` §7.1 nennt die wahrscheinlichsten Ursachen. |

### Weitergabe an deinen Freund (später)

```powershell
dotnet publish src/AppControl.Host/AppControl.Host.csproj `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -o publish
```

Ergebnis: eine einzelne `publish\AppControl.exe` (~70 MB), die ohne installierte
.NET-Runtime läuft. Daneben die `appsettings.json` mit deiner Signaling-URL:

```json
{
  "signalingUrl": "wss://appcontrol-signal-DEINNAME.fly.dev/ws",
  "defaultMaxSessionMinutes": 30,
  "controlIdleTimeoutMinutes": 5,
  "consentTimeoutSeconds": 60
}
```

Beides in einen Ordner, zippen, fertig. Kein Installer, kein Registry-Eintrag,
kein Autostart — das ist Absicht, siehe
[`docs/03-consent-and-transparency.md` §3.9](docs/03-consent-and-transparency.md).

---

## Schritt 4 — Die erste Verbindung

Sobald Schritt 3 vollständig ist:

**Auf dem Windows-Rechner (dein Freund):**
1. `AppControl.exe` starten — Dashboard und Tray-Icon erscheinen
2. „Neue Einladung erzeugen" → Code kopieren
3. Code an dich schicken — **über Signal, iMessage oder Telefon**, nicht über den
   Signaling-Server. Wer den Code hat, kann eine Verbindung anfragen.
4. „Auf Verbindung warten" klicken

**Auf deinem Mac:**
5. Viewer starten, Code einfügen, „Verbinden"

**Beide gleichzeitig:**
6. Auf beiden Bildschirmen erscheinen **fünf Emojis**. Lest sie euch am Telefon vor.
   Stimmen sie nicht überein → **abbrechen**, dann hat sich jemand dazwischengeschaltet.

**Wieder auf Windows:**
7. Dein Freund sieht den Consent-Dialog: Was freigeben (ganzer Bildschirm oder eine
   App), ob du steuern darfst (Standard: **nein**), wie lange.
8. „Freigeben" → Stream startet, das rote Overlay erscheint bei ihm

**Was dein Freund jederzeit tun kann:**
- Overlay-Button „⏸ Pause" oder „⏹ Beenden"
- Tray-Rechtsklick → Sofort beenden
- `Strg+Alt+Umschalt+X`
- Dreimal schnell die rechte Strg-Taste

---

## Wenn etwas nicht funktioniert

| Symptom | Ursache | Lösung |
|---|---|---|
| Viewer bleibt bei „Verbinde …" | Signaling-URL falsch oder Server schläft | `curl https://<server>/health` |
| Beide verbunden, aber kein Bild | ICE findet keinen P2P-Pfad | TURN einrichten ([§6.1](docs/06-build-and-run.md)); im Viewer prüfen, ob „Über Relay" steht |
| Emojis unterschiedlich | Möglicher MitM — oder verschiedene Programmversionen | **Nicht fortfahren.** Erst `python3 tools/consistency/check.py` |
| Host: „Code enthält einen Tippfehler" | Ticket unvollständig kopiert | Ganzen Code inklusive `AC1-`-Präfix |
| Eingaben kommen nicht an | Gate 3: freigegebenes Fenster nicht im Vordergrund | Dein Freund muss das Fenster anklicken |

Vollständige Liste: [`docs/06-build-and-run.md` §6.7](docs/06-build-and-run.md).
