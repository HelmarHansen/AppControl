# 4 — Protokollspezifikation

Sprachneutrale Definition aller Nachrichten. Host (C#), Viewer (Swift) und Signaling-Server
(TypeScript) implementieren jeweils diese Spezifikation; sie ist die verbindliche Quelle.

Version: **`ac/1`**. Beide Seiten senden ihre Protokollversion im `hello`; bei Nichtübereinstimmung
der Major-Version wird die Verbindung mit `bye { reason: "protocol-mismatch" }` beendet.

---

## 4.1 Schicht 1 — Signaling (WebSocket, JSON)

Verbindung: `wss://<server>/ws`. Der Server kennt nur Räume und leitet weiter.

### Client → Server

```jsonc
// Raum betreten. roomId ist die öffentliche Hälfte des Pairing-Tickets.
{ "t": "join", "roomId": "b32:JBSWY3DPEHPK3PXP", "role": "host" | "viewer" }

// Undurchsichtiger Umschlag an den jeweils anderen Teilnehmer.
// Der Server sieht NIEMALS den Klartext von `payload`.
{ "t": "relay", "payload": "<base64 ciphertext>" }

// Raum verlassen
{ "t": "leave" }
```

### Server → Client

```jsonc
{ "t": "joined",      "roomId": "…", "peerPresent": false }
{ "t": "peer-joined" }
{ "t": "peer-left" }
{ "t": "relay",       "payload": "<base64 ciphertext>" }
{ "t": "ice-servers", "servers": [ { "urls": ["turn:…"], "username": "…", "credential": "…" } ] }
{ "t": "error",       "code": "room-full" | "rate-limited" | "bad-request" | "unauthorized",
                      "message": "…" }
```

**Garantien des Servers:** maximal zwei Teilnehmer pro Raum; Weiterleitung ohne Interpretation;
keine Persistenz; Rate-Limit pro Verbindung; Räume verfallen 10 Minuten nach dem Leeren.

---

## 4.2 Schicht 2 — Verschlüsselte Umschläge

Jeder `relay.payload` ist ein serialisierter Umschlag. Zwei Phasen mit unterschiedlichen Schlüsseln:

```
Phase A (Handshake):  Schlüssel = HKDF(psk,  info="ac/1 handshake")
Phase B (Session):    Schlüssel = HKDF(dh_1‖dh_2‖dh_3, info="ac/1 session")
```

Binärformat des Umschlags:

```
Offset  Größe  Feld
0       4      Magic "AC1\0"
4       1      Phase: 0x01 = Handshake, 0x02 = Session
5       1      Richtung: 0x01 = host→viewer, 0x02 = viewer→host
6       2      reserviert (0)
8       12     Nonce (Zähler 8 Byte big-endian ‖ 4 Byte zufälliger Salt)
20      N      ChaCha20-Poly1305-Ciphertext (inkl. 16-Byte-Tag)
```

Bytes 0–19 sind **Associated Data** der AEAD. Der Nonce-Zähler ist pro Richtung streng monoton
steigend; ein Umschlag mit einem Zähler ≤ dem zuletzt akzeptierten wird verworfen (Replay-Schutz).

Klartext-Inhalt ist UTF-8-JSON, siehe 4.3.

---

## 4.3 Schicht 3 — Handshake und Session-Setup

### Handshake (Phase A)

```jsonc
// 1) Host → Viewer  UND  2) Viewer → Host  (symmetrisch, Reihenfolge egal)
{
  "t": "hs",
  "v": "ac/1",
  "ephemeral": "<base64 32B X25519 pubkey>",
  "static":    "<base64 32B X25519 pubkey>",   // Langzeit-Identität
  "name":      "Jakobs MacBook Pro",
  "nonce":     "<base64 16B>"                   // fließt in die SAS-Ableitung ein
}
```

Beide Seiten berechnen nach Erhalt der Gegenseite:

```
dh_1 = X25519(e_eigen,  e_fremd)     # Ephemeral–Ephemeral  → Forward Secrecy
dh_2 = X25519(e_eigen,  s_fremd)     # Ephemeral–Static      → authentifiziert die Gegenseite
dh_3 = X25519(s_eigen,  e_fremd)     # Static–Ephemeral      → authentifiziert uns
                                      # (Zuordnung nach Rolle, siehe 05-security.md §5.3)

session_key = HKDF-SHA256(
    ikm    = dh_1 ‖ dh_2 ‖ dh_3,
    salt   = SHA256(nonce_host ‖ nonce_viewer),
    info   = "ac/1 session",
    length = 32)

sas_bytes   = HKDF-SHA256(session_key, info="ac/1 sas", length = 5)
sas_emoji   = [ EMOJI_TABLE[b % 64] for b in sas_bytes ]     # 64er-Tabelle → 30 Bit
```

### Session (Phase B)

```jsonc
{ "t": "sdp",  "kind": "offer" | "answer", "sdp": "v=0\r\n…" }
{ "t": "ice",  "candidate": "candidate:…", "sdpMid": "0", "sdpMLineIndex": 0 }
{ "t": "consent-request",  "wantsControl": true, "viewerName": "Jakob" }
{ "t": "consent-response", "granted": true,
                           "scope": { "kind": "window", "title": "Visual Studio",
                                      "width": 1920, "height": 1080 },
                           "controlGranted": false,
                           "maxDurationSec": 1800 }
{ "t": "bye", "reason": "user-stopped" | "timeout" | "denied" | "protocol-mismatch" | "error" }
```

Der SDP enthält den DTLS-Fingerprint. Weil er nur innerhalb des mit `session_key` verschlüsselten
Umschlags reist, kann der Signaling-Server ihn nicht ersetzen — das ist die Bindung zwischen
authentifiziertem Handshake und Medienverschlüsselung.

---

## 4.4 Schicht 4 — DataChannel `ac-control` (JSON, reliable ordered)

Nach Aufbau der PeerConnection läuft die Steuerung über den DataChannel statt über das Signaling.

```jsonc
// Host → Viewer: Zustandsänderung. Wird bei JEDER Änderung gesendet.
{ "t": "share-state",
  "state": "sharing" | "paused" | "stopped",
  "scope": { "kind": "window" | "screen", "title": "Visual Studio",
             "width": 1920, "height": 1080 },
  "controlGranted": false,
  "elapsedSec": 724,
  "remainingSec": 1076 }

// Host → Viewer: Steuerung erteilt/entzogen (auch als eigenes Event, für sofortige UI-Reaktion)
{ "t": "control-state", "granted": false, "reason": "host-revoked" | "idle-timeout"
                                                  | "window-not-focused" | "host-paused" }

// Host → Viewer: Ein Event wurde abgelehnt — der Viewer soll erklären können, warum
{ "t": "input-rejected", "gate": 3, "reason": "target-window-not-foreground", "count": 47 }

// Host → Viewer: Cursorform, damit der Viewer den passenden Zeiger zeigt
{ "t": "cursor", "shape": "arrow" | "ibeam" | "hand" | "resize-ns" | "resize-ew" | "wait",
                 "visible": true }

// Viewer → Host: Bitte um Steuerung (Host muss erneut bestätigen)
{ "t": "request-control" }

// Viewer → Host: Viewport-Größe geändert → Host passt Encoder-Auflösung an
{ "t": "viewport", "width": 1600, "height": 900, "scale": 2.0 }

// Viewer → Host: Keyframe anfordern (zusätzlich zu RTCP-PLI)
{ "t": "request-keyframe" }

// beidseitig: Latenzmessung
{ "t": "ping", "id": 42, "tsMicros": 1712345678901234 }
{ "t": "pong", "id": 42, "tsMicros": 1712345678901234 }

// beidseitig: Telemetrie für die Statusanzeige
{ "t": "stats", "fps": 58, "bitrateKbps": 4200, "rttMs": 18,
                "encodeMs": 3.1, "packetsLost": 0 }

{ "t": "bye", "reason": "user-stopped" }
```

**Wichtig:** `share-state` ist die einzige Quelle für das, was der Viewer anzeigt. Der Viewer
leitet nichts aus dem Videostrom ab. Wenn der Host pausiert, zeigt der Viewer das Standbild mit
Banner, *weil* `share-state.state == "paused"` ankam — nicht, weil keine Frames mehr eintreffen.
Ein stehendes Bild wegen Netzproblemen sieht sonst identisch aus wie eine bewusste Pause, und der
Unterschied ist genau das, worum es bei Transparenz geht.

---

## 4.5 Schicht 4 — DataChannels `ac-input` / `ac-cursor` (binär)

Binär statt JSON, weil bei 200 Events/s das Parsen und die Größe messbar werden. Alle
Mehrbyte-Werte **little-endian**.

### Gemeinsamer Header (6 Byte)

```
Offset  Typ     Feld
0       u8      msgType
1       u8      flags       (Bit 0: fromCGEventTap, Bits 1–7: reserviert)
2       u32     seq         monoton steigend, gemeinsam über beide Kanäle
```

`seq` über beide Kanäle hinweg erlaubt dem Host, verlorene Cursor-Pakete zu erkennen und
Reihenfolgen korrekt zu rekonstruieren, obwohl sie über unterschiedliche Kanäle kommen.

### `0x01 MOUSE_MOVE` → `ac-cursor` (unreliable)

```
6       f32     x            normalisiert 0.0–1.0, relativ zum geteilten Inhalt
10      f32     y
14      u32     tsMillis     Zeitstempel des Viewers (relativ)
                             Gesamt: 18 Byte
```

### `0x02 MOUSE_BUTTON` → `ac-input` (reliable)

```
6       f32     x
10      f32     y
14      u8      button       0=links 1=rechts 2=mitte 3=back 4=forward
15      u8      pressed      0=hoch 1=runter
16      u16     clickCount   für Doppel-/Dreifachklick
                             Gesamt: 18 Byte
```

### `0x03 MOUSE_SCROLL` → `ac-input` (reliable)

```
6       f32     x
10      f32     y
14      f32     deltaX       positive Werte = rechts
18      f32     deltaY       positive Werte = hoch (macOS-Konvention; Host invertiert)
22      u8      precise      1 = Trackpad (Pixel), 0 = Rad (Ticks)
                             Gesamt: 23 Byte
```

### `0x04 KEY` → `ac-input` (reliable)

```
6       u16     hidUsage     HID Usage-ID (Usage Page 0x07) — plattformneutral!
8       u8      pressed      0=hoch 1=runter
9       u8      modifiers    Bit0 Shift · Bit1 Ctrl · Bit2 Alt/Option · Bit3 Meta/Cmd/Win
10      u8      isRepeat
                             Gesamt: 11 Byte
```

**Warum HID Usage-IDs?** macOS liefert `CGKeyCode` (ANSI-Layout-basiert), Windows will
`VK_*`/Scancodes. Eine direkte Umrechnung müsste beide Layouts kennen. HID Usage-IDs sind der
gemeinsame Standard *unterhalb* beider Systeme: `0x04` ist die Taste an der Position von „A" auf
einem US-Layout, unabhängig davon, was daraufgedruckt ist. Der Viewer bildet `CGKeyCode → HID` ab,
der Host `HID → Scancode`, und Windows wendet das *Host*-Tastaturlayout an. Ergebnis: Der Host
bekommt das, was seine eigene Tastaturbelegung erzeugen würde — genau das gewünschte Verhalten,
wenn man in einem fremden System tippt.

`Input/HidKeyMap.cs` und `Input/HidKeyMap.swift` enthalten die Tabellen; die Round-Trip-Tests
prüfen sie gegeneinander.

### `0x05 TEXT` → `ac-input` (reliable)

```
6       u16     byteLength
8       …       UTF-8-Bytes
```

Für Zeichen, die über Tastencodes nicht sinnvoll darstellbar sind (Umlaute über Option-Tasten,
Emoji, IME-Eingaben aus dem macOS-Zeichenpalette). Der Host injiziert sie über
`KEYEVENTF_UNICODE`, wodurch das Zeichen direkt eingefügt wird, ohne Tastaturlayout-Umweg.

### `0x06 RELEASE_ALL` → `ac-input` (reliable)

Kein Payload. Der Viewer sendet das, wenn sein Fenster den Fokus verliert. Der Host gibt alle
gehaltenen Tasten und Buttons frei. Ohne diese Nachricht bliebe eine Taste hängen, wenn der Nutzer
mitten im Tastendruck zu einer anderen macOS-App wechselt.

---

## 4.6 Koordinatensystem

Alle Positionen sind `f32` im Bereich `[0.0, 1.0]`, relativ zur **oberen linken Ecke des
freigegebenen Inhalts**, y nach unten.

Der Viewer rechnet um: `x_norm = (mouse_x - video_frame_x) / video_frame_width`.
Der Host rechnet zurück: bei Fenster-Scope auf das Client-Rect, bei Screen-Scope auf das
Monitor-Rect — und dann in `SendInput`-Absolutkoordinaten über den *virtuellen* Desktop.

Der Grund für Normalisierung statt Pixeln: Auflösung, DPI-Skalierung und Fenstergröße können sich
mitten in der Sitzung ändern (Fenster wird verschoben, Encoder skaliert herunter, Viewer ändert
Fenstergröße). Normalisierte Koordinaten sind gegen all das immun, und die Umrechnung findet
genau an den zwei Stellen statt, die den jeweils aktuellen Zustand kennen.

Werte außerhalb `[0,1]` werden vom Host geklemmt, nicht abgelehnt (siehe Gate 4).

---

## 4.7 Videostrom

| Parameter | Wert |
|---|---|
| Codec | H.264, Baseline/Main, Level nach Auflösung |
| Transport | RTP über SRTP (WebRTC-Medien-Track) |
| Packetization | RFC 6184 Mode 1 (STAP-A / FU-A) |
| Framerate | adaptiv 15–60 fps |
| Bitrate | adaptiv 1–15 Mbit/s, gesteuert von WebRTC-GCC |
| Keyframes | **nur auf Anfrage** (RTCP-PLI oder `request-keyframe`) |
| B-Frames | keine |
| Auflösung | Quellauflösung, herunterskaliert auf max. Viewport (`viewport`-Nachricht) |

---

## 4.8 Fehlerbehandlung und Verbindungsverlust

| Ereignis | Host-Verhalten | Viewer-Verhalten |
|---|---|---|
| DataChannel schließt | Sitzung sofort beenden (Fail-Closed) | „Verbindung verloren" anzeigen |
| ICE `disconnected` | Capture **pausieren**, 15 s auf Recovery warten | „Verbindungsproblem" |
| ICE `failed` | Sitzung beenden | „Verbindung fehlgeschlagen" |
| Kein `ping`-Reply > 10 s | Steuerung entziehen, dann beenden | erneut verbinden anbieten |
| Ungültiger Umschlag (AEAD-Fehler / Replay) | verwerfen, zählen; > 10 in 60 s → beenden | dito |
| Unbekannter `msgType` | ignorieren (Vorwärtskompatibilität) | ignorieren |

**Fail-Closed ist durchgehend das Prinzip:** Jede Unsicherheit über den Verbindungszustand führt
dazu, dass weniger passiert, nicht mehr. Ein Host, dessen Verbindung wackelt, hört auf zu streamen;
er streamt nicht weiter in der Hoffnung, dass es schon gut geht.
