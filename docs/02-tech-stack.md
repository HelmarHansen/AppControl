# 2 — Tech-Stack: Entscheidungen und Begründung

Jeder Abschnitt nennt die **Entscheidung**, den **Grund**, und die **verworfenen Alternativen** mit
dem Kriterium, an dem sie gescheitert sind. Wo eine Alternative unter anderen Randbedingungen die
bessere Wahl wäre, steht das dabei.

---

## 2.1 Screen-Capture auf Windows

### Entscheidung: `Windows.Graphics.Capture` (WGC)

WGC ist seit Windows 10 1803 die vom Betriebssystem vorgesehene Capture-API und liefert Frames als
`Direct3D11CaptureFrame` — also als GPU-Textur, die den Grafikspeicher nie verlässt, bis der
Encoder sie liest.

**Warum WGC und nicht etwas anderes:**

1. **Zero-Copy bis in den Encoder.** Der Weg ist `Compositor → IDirect3DSurface → MF-Encoder → NAL`.
   Kein Download in den Systemspeicher, keine CPU-Konvertierung. Das ist der einzige Weg, bei dem
   4K@60 realistisch bleibt.
2. **Fenster-Capture, die tatsächlich funktioniert.** WGC kann ein einzelnes `HWND` erfassen, auch
   wenn es teilweise verdeckt, minimiert-wiederhergestellt oder hardwarebeschleunigt ist
   (Chrome, Electron, Spiele). `BitBlt`/`PrintWindow` scheitern genau daran regelmäßig.
3. **Der gelbe Rahmen ist ein Feature, kein Ärgernis.** Windows zeichnet um jedes per WGC erfasste
   Fenster eine gelbe Umrandung, die die Anwendung **nicht** abschalten kann. Für Leitplanke 2
   ("nichts passiert unsichtbar") ist das ein vom Betriebssystem garantierter Transparenzhinweis —
   stärker als alles, was wir selbst bauen könnten, weil unser eigenes Overlay theoretisch
   manipulierbar wäre, dieser Rahmen aber nicht.
   *(Seit Windows 11 lässt sich der Rahmen per `IsBorderRequired = false` ausschalten — dafür ist
   allerdings die eingeschränkte Capability `graphicsCaptureWithoutBorder` nötig. AppControl fragt
   sie nicht an und setzt die Property nicht. Siehe Kapitel 3.)*
4. **Kein Treiber, keine Admin-Rechte, kein Hook.** Läuft als normaler Benutzerprozess.

**Verworfene Alternativen:**

| Alternative | Warum nicht |
|---|---|
| **Desktop Duplication API** (DXGI) | Technisch exzellent für Vollbild und ähnlich schnell — kann aber grundsätzlich **kein einzelnes Fenster** erfassen. Die App-Freigabe ist eine Kernanforderung, also scheidet sie als Hauptweg aus. Bleibt als Fallback für Vollbild auf Windows-Versionen < 1803 sinnvoll. |
| **GDI `BitBlt` / `PrintWindow`** | Liefert bei GPU-beschleunigten Fenstern schwarze Flächen, ist CPU-gebunden und langsam. Für dieses Projekt unbrauchbar. |
| **DirectX-Hooking** (wie Spiele-Overlays) | Injiziert Code in fremde Prozesse. Nicht vertretbar in einem Tool, dessen ganzer Zweck Vertrauenswürdigkeit ist — und ein zuverlässiger Weg, von Anti-Cheat und EDR als Malware klassifiziert zu werden. |
| **OBS als Backend** (obs-studio via WebSocket) | Riesige Abhängigkeit, und die App-Auswahl wäre in OBS statt in unserer UI — damit läge die Consent-Anzeige außerhalb unserer Kontrolle. |

### Entscheidung: Media Foundation H.264 für die Kompression

Der Encoder wird als **Media Foundation Transform (MFT)** angesprochen, was auf allen relevanten
GPUs (Intel QuickSync, NVIDIA NVENC, AMD VCE) automatisch die Hardware-Einheit nutzt.

Konfiguration, die für Remote-Control zählt:

```
Profil:              Baseline oder Main (Low-Latency-Pfad)
Rate-Control:        CBR, 2–12 Mbit/s, dynamisch angepasst
B-Frames:            0          ← kritisch: B-Frames brauchen künftige Frames = +1 Frame Latenz
GOP:                 unendlich  ← kein periodischer Keyframe; stattdessen on-demand per PLI
LowLatencyMode:      true       ← Encoder puffert nicht mehrere Frames für Lookahead
```

**Kein periodischer Keyframe** ist die unintuitivste dieser Einstellungen. Übliche Streams senden
alle 2 Sekunden einen I-Frame; der ist 10–30× größer als ein P-Frame und erzeugt einen
periodischen Latenz-Spike. Bei einer Punkt-zu-Punkt-Verbindung mit Rückkanal ist das unnötig: Der
Viewer meldet Paketverlust per RTCP-PLI, und *nur dann* erzeugt der Host einen Keyframe. Bei einer
sauberen Verbindung fließt nach dem allerersten I-Frame nur noch Delta.

**Codec-Alternativen:**

| Codec | Bewertung |
|---|---|
| **H.264** ✅ | Hardware-Encoder auf praktisch jeder Windows-GPU seit ~2012, Hardware-Decoder in jedem Mac seit Ivy Bridge. Universeller geht es nicht. |
| **H.265/HEVC** | ~30 % weniger Bitrate bei gleicher Qualität, Apple-Silicon-Macs dekodieren es in Hardware. Aber: WebRTC-Unterstützung ist lückenhaft, Lizenzlage unangenehm. Als optionaler Codec später sinnvoll, wenn beide Seiten ihn aushandeln. |
| **AV1** | Bestbietende Kompression, aber Hardware-Encoding erst ab Intel Arc / RTX 40 / RDNA3. Software-AV1 ist für Echtzeit zu langsam. In 3–4 Jahren die richtige Wahl. |
| **VP8/VP9** | Universelle WebRTC-Unterstützung, aber auf Windows fast nie hardwarebeschleunigt → CPU-Last. Sinnvoll nur als Notfall-Fallback. |

Der Code ist deshalb hinter `IVideoEncoder` abstrahiert (`Encoding/IVideoEncoder.cs`) — der Codec
ist austauschbar, ohne die Capture- oder Netzwerkschicht anzufassen.

---

## 2.2 Input-Simulation auf Windows

### Entscheidung: `SendInput` (user32.dll)

`SendInput` ist die dokumentierte, unterstützte Win32-API zum Einspeisen synthetischer Eingaben in
den Systemeingabestrom.

**Warum:**

- Events durchlaufen dieselbe Pipeline wie echte Hardware-Eingaben — Anwendungen können sie nicht
  von Benutzereingaben unterscheiden, was gerade der Punkt ist. `PostMessage(WM_KEYDOWN)` dagegen
  umgeht den Tastaturzustand komplett, sodass Modifier (Shift, Strg) in vielen Anwendungen ignoriert
  werden.
- Events aus einem `SendInput`-Aufruf werden **atomar** eingefügt: Zwischen "Shift runter" und
  "A runter" kann sich kein echtes Benutzer-Event drängeln. Ohne diese Garantie entstünden unter
  Last vertauschte Modifier.
- Kein Treiber, keine Admin-Rechte, keine Signierung nötig.

**Die zwei Fallstricke, die im Code adressiert sind:**

1. **Koordinaten sind nicht Pixel.** `MOUSEEVENTF_ABSOLUTE` erwartet einen normalisierten Bereich
   von 0–65535 über den *virtuellen* Desktop (alle Monitore zusammen), nicht über den primären.
   Bei Multi-Monitor-Setups ist das die häufigste Fehlerquelle. `Input/InputInjector.cs`
   rechnet über `SM_XVIRTUALSCREEN`/`SM_CXVIRTUALSCREEN` korrekt um.
2. **DPI.** Ohne `PerMonitorV2`-DPI-Awareness liefert Windows dem Prozess virtualisierte
   Koordinaten, und Klicks landen auf skalierten Displays daneben. Das ist in `app.manifest`
   deklariert.

**Verworfene Alternativen:**

| Alternative | Warum nicht |
|---|---|
| `keybd_event` / `mouse_event` | Von Microsoft als veraltet markiert, keine Atomizität, keine korrekte Multi-Monitor-Behandlung. |
| `PostMessage`/`SendMessage` direkt ans Fenster | Verlockend, weil scheinbar auf ein Fenster begrenzt — aber der Tastaturzustand (`GetKeyState`) wird nicht mitgeführt, sodass Modifier, IME und viele Frameworks (Qt, Electron, Spiele) nicht reagieren. Unzuverlässig. |
| **Virtueller HID-Treiber** (z. B. ViGEm-Ansatz) | Funktioniert auch auf UAC-Prompts und dem sicheren Desktop — genau *deshalb* nicht: Ein Tool, das Eingaben an der UAC-Sicherheitsgrenze vorbei einschleusen kann, ist kategorisch etwas anderes als ein Screen-Sharing-Tool. Braucht außerdem signierte Treiber und Admin-Installation. |
| `SetWindowsHookEx` mit Injection | Codeinjektion in fremde Prozesse; siehe oben. |

**Bewusste Grenze:** AppControl kann nicht in erhöhte Prozesse (Admin-Fenster), den UAC-Prompt,
den Sperrbildschirm oder Ctrl+Alt+Del injizieren. Das ist eine Windows-Sicherheitsgrenze
(UIPI/Secure Desktop) — und wir umgehen sie nicht. Ist das Ziel ein erhöhtes Fenster, muss der
Host selbst handeln. Der Viewer bekommt dafür eine erklärende Meldung statt stillem Nichtstun.

---

## 2.3 Host-UI: .NET 8 + WPF

### Entscheidung: WPF auf `net8.0-windows10.0.19041.0`

Das Target-Framework-Moniker ist der eigentliche Trick: Es aktiviert **C#/WinRT-Interop**, sodass
`Windows.Graphics.Capture` (eine WinRT-API) direkt aus WPF heraus nutzbar ist — ohne Wrapper,
ohne WinUI, ohne MSIX-Paketierung.

**Warum WPF und nicht WinUI 3:**

| Kriterium | WPF | WinUI 3 |
|---|---|---|
| WinRT-Capture-APIs | ✅ über TFM | ✅ nativ |
| Always-on-top, click-through Overlay | ✅ trivial (`Topmost`, `WS_EX_TRANSPARENT`) | ⚠️ umständlich, Fensterverwaltung ist eingeschränkt |
| Tray-Icon | ✅ `NotifyIcon` (WinForms-Interop) | ⚠️ braucht Drittbibliothek oder P/Invoke |
| Verteilung | ✅ Single-File-EXE, `dotnet publish` | ⚠️ Windows App SDK Runtime oder Self-contained-Bloat |
| Globaler Hotkey / Low-Level-Hook | ✅ direkt | ✅ direkt |

Für eine Anwendung, deren Kernanforderungen "immer sichtbares Overlay", "Tray-Icon" und "einfach
weiterzugeben" lauten, gewinnt WPF in jeder Zeile. WinUI 3 wäre die Wahl, wenn moderne Fluent-Optik
oder Store-Verteilung im Vordergrund stünden — tut sie hier nicht.

**Verworfene Alternativen:**

- **Electron/Tauri:** Der Capture- und Encoder-Pfad müsste über eine native Node-Addon- bzw.
  Rust-Brücke laufen. Man handelt sich die Komplexität beider Welten ein und gewinnt nichts, weil
  es ohnehin nur eine Windows-Version gibt.
- **C++/WinRT nativ:** Schnellster Weg, sauberste API-Nutzung — aber die UI-Arbeit (Overlay,
  Picker mit Live-Thumbnails, Consent-Dialog) kostet ein Vielfaches. Für einen Skeleton, den man
  erweitern soll, ist C# der bessere Ausgangspunkt. Die kritischen Pfade (Capture, Encode) sind
  ohnehin nur dünne Interop-Schichten über nativem Code.
- **Rust + `windows-rs`:** Sehr attraktiv, und ein späterer Port des Capture/Encode-Kerns wäre
  sinnvoll. Aber die WPF-UI-Produktivität für den Consent-Layer wiegt hier schwerer.

---

## 2.4 macOS-Viewer: Swift 5.9 + SwiftUI

### Entscheidung: SwiftUI-App mit `RTCMTLNSVideoView` zum Rendern

**Rendering-Pfad:** `SRTP → WebRTC-Jitterbuffer → VideoToolbox (HW-Decode) → CVPixelBuffer → Metal`

`RTCMTLNSVideoView` aus dem WebRTC-Framework rendert `CVPixelBuffer` direkt über Metal, ohne
Umweg über CPU-Speicher oder Core Animation. Auf Apple Silicon ist das der kürzeste existierende
Weg von Netzwerkpaket zu Pixel.

**Warum nicht selbst dekodieren?** Man *könnte* `VTDecompressionSession` direkt ansteuern und in
einen eigenen `CAMetalLayer` rendern. Das ergibt Sinn, wenn man den Jitter-Buffer selbst bauen
will (siehe 2.5, Alternative "custom QUIC"). Solange WebRTC den Transport macht, wäre es doppelte
Arbeit mit identischem Ergebnis. `Video/VideoRenderView.swift` kapselt das Rendering hinter einem
Protokoll, sodass ein Austausch lokal bleibt.

### Input-Erfassung: zwei Ebenen

| Ebene | API | Erfasst | Berechtigung |
|---|---|---|---|
| **Standard** | `NSEvent.addLocalMonitorForEvents` | Alle Events, die an unser Fenster gehen | keine |
| **Erweitert** | `CGEvent.tapCreate` | Zusätzlich systemweit abgefangene Tasten (Cmd+Tab, Cmd+Q, F-Tasten) | **Bedienungshilfen** in den Systemeinstellungen |

Der lokale Monitor ist der Standardweg und braucht keinerlei Sonderrechte — er reicht für alles,
was während der Session in unserem Fenster passiert. Der `CGEventTap` ist **opt-in** und nur dafür
da, dass Tastenkombinationen, die macOS sonst selbst abfängt, an den Windows-Host durchgereicht
werden können. Die App funktioniert ohne ihn vollständig; `Input/InputCapture.swift` startet den
Tap nur, wenn der Nutzer ihn aktiviert und die Berechtigung erteilt hat.

**Verworfene Alternative — Viewer als Web-App:** Ein Browser-Viewer wäre mit WebRTC trivial und
plattformunabhängig. Er scheitert aber an der Input-Erfassung: Ein Browser kann Cmd+Tab, Cmd+Q,
F11 oder die Escape-Taste im Fullscreen nicht abfangen, und die Pointer-Lock-API verhält sich beim
Verlassen des Fensters unvorhersehbar. Für ein Werkzeug, dessen Zweck Steuerung ist, ist das
disqualifizierend. Als reiner *Zuschauer*-Modus wäre eine Web-Variante eine sinnvolle Ergänzung.

---

## 2.5 Netzwerk-Transport: WebRTC

### Entscheidung: WebRTC (DTLS-SRTP + SCTP-DataChannels), P2P mit TURN-Fallback

Das ist die folgenreichste Entscheidung des Projekts, deshalb ausführlicher.

**Was WebRTC mitbringt, das wir sonst selbst bauen müssten:**

1. **NAT-Traversal (ICE).** Zwei Privatanschlüsse hinter unterschiedlichen Routern direkt zu
   verbinden ist das eigentlich schwierige Problem. ICE probiert systematisch Host-Kandidaten,
   STUN-reflexive Adressen und TURN-Relays durch und findet in der Praxis in ~85 % der Fälle einen
   direkten Weg. Das selbst zu bauen bedeutet, ICE nachzubauen — es gibt keine kürzere Version.
2. **Verschlüsselung ist nicht abschaltbar.** DTLS-SRTP ist im Standard verpflichtend. Es gibt
   keinen Konfigurationsfehler, der zu einem Klartext-Stream führt.
3. **Congestion Control (GCC).** Schätzt laufend die verfügbare Bandbreite und meldet sie an den
   Encoder. Ohne das erzeugt man entweder ein zu konservatives, ständig unscharfes Bild oder
   überfährt die Leitung und die Latenz explodiert. Eine eigene Implementierung ist Monate Arbeit.
4. **Jitter-Buffer, RTCP, PLI/FIR/NACK, Bandbreitenanpassung** — alles vorhanden und aufeinander
   abgestimmt.

**Bibliotheken:**

- Windows: **SIPSorcery** (MIT, rein verwaltetes C#). Kein natives Build, kein 200-MB-Blob.
  Unterstützt H.264-Passthrough, sodass unser Media-Foundation-Encoder direkt einspeist.
- macOS: **`stasel/WebRTC`** (SwiftPM-Distribution des offiziellen Google-`WebRTC.xcframework`).
  Die kanonische Implementierung, mit `RTCMTLNSVideoView` inklusive.

**Verworfene Alternativen:**

| Alternative | Bewertung |
|---|---|
| **Eigenes Protokoll über QUIC** (`quinn` / `MsQuic`) | Sehr reizvoll: volle Kontrolle über Latenz, kein SDP, Streams statt Kanäle, und QUIC-Datagramme sind für Input ideal. **Scheitert an NAT-Traversal** — QUIC-Hole-Punching ist ohne ICE-Äquivalent unzuverlässig. Man würde ICE nachbauen und hätte am Ende WebRTC ohne dessen Reife. *Die richtige Wahl, wenn beide Seiten in einem bekannten Netz oder über ein VPN/Tailscale erreichbar wären.* |
| **RDP** | Für Windows→Windows unschlagbar (semantisches Protokoll statt Video). Aber App-Freigabe (RemoteApp) erfordert Server-Editionen oder Hacks, macOS-Clients sind Fremdimplementierungen, und das Consent-Modell wäre Microsofts, nicht unseres. |
| **VNC/RFB** | Einfach und offen — aber pixel-diff-basiert, ohne Videocodec. Bei Video oder Scrollen bricht es zusammen. Keine eingebaute NAT-Traversal, Verschlüsselung nur per Erweiterung. |
| **HLS/DASH** | Für Live-Streaming an viele Zuschauer gebaut, Latenz 2–30 s. Für Interaktion unbrauchbar. |
| **Fertige SaaS-SDKs** (LiveKit, Daily, Agora) | Würde die Arbeit stark verkürzen, bedeutet aber Abhängigkeit von einem Dritten im Medienpfad und widerspricht Leitplanke 3. |

### Signaling: eigener WebSocket-Rendezvous (Node/TypeScript)

Ein absichtlich winziger Server: WebSocket rein, `roomId` merken, Nachrichten zwischen genau zwei
Teilnehmern weiterleiten. **Kein Konto, keine Datenbank, keine Historie.** Der Server kann den
Inhalt nicht lesen (alles ist mit dem Pairing-Schlüssel verschlüsselt) und speichert nichts.

Warum eigener Server statt Firebase o. ä.: Er ist ~300 Zeilen, in fünf Minuten auf einem 3-€-VPS
oder Fly.io deployt, und wir wollen keine dritte Partei, die weiß, wer wann mit wem verbindet.

---

## 2.6 Kryptografie

### Entscheidung: X25519 Triple-DH-Handshake, ChaCha20-Poly1305, HKDF-SHA256

| Zweck | Primitive | Windows | macOS |
|---|---|---|---|
| Langzeit-Identität | X25519 (statisches Schlüsselpaar) | NSec | swift-crypto |
| Schlüsselaustausch | X25519 ECDH, dreifach kombiniert | NSec | swift-crypto |
| Schlüsselableitung | HKDF-SHA256 | NSec | swift-crypto |
| Verschlüsselung | ChaCha20-Poly1305 (AEAD) | NSec | swift-crypto |
| Medienverschlüsselung | DTLS-SRTP (von WebRTC) | SIPSorcery | WebRTC.framework |

`NSec.Cryptography` ist ein dünner, gut gepflegter .NET-Wrapper über **libsodium**;
`swift-crypto` ist Apples Portierung von **BoringSSL**-Primitiven mit derselben API wie CryptoKit.
Beide implementieren exakt dieselben Standards, was Interoperabilität ohne Überraschungen
ermöglicht — verifiziert über gemeinsame Testvektoren in `tools/crypto-vectors/`.

**Warum ChaCha20-Poly1305 statt AES-GCM:** Konstantzeit-Implementierung ohne
Hardware-Unterstützung, keine Timing-Angriffsfläche durch Tabellen-Lookups, und es gibt keinen
katastrophalen Nonce-Reuse-Fallstrick in derselben Schärfe wie bei GCM. Auf Hardware mit AES-NI
wäre AES-GCM schneller — irrelevant, weil wir damit nur Kilobytes an Signaling verschlüsseln, nicht
den Medienstrom.

**Warum kein PAKE (SPAKE2/OPAQUE) für das Pairing:** Ein PAKE erlaubt einen *kurzen* Code
("123456") ohne Offline-Wörterbuchangriff. Das wäre die schönere UX. Aber weder NSec noch
swift-crypto exponieren die dafür nötigen Gruppenoperationen (Hash-to-Curve, Punkt-Addition), und
selbstgeschriebene PAKE-Implementierungen sind eine klassische Quelle subtiler, katastrophaler
Fehler. Die gewählte Alternative — ein **hochentropischer Einmal-Code (256 Bit)**, den die beiden
Freunde ohnehin über einen bestehenden sicheren Kanal (Signal, iMessage) austauschen — erreicht
dasselbe Sicherheitsniveau ohne eigene Krypto-Konstruktion. Details und der Migrationspfad zu
CPace in [`05-security.md`](05-security.md).

---

## 2.7 Zusammenfassung

| Schicht | Windows-Host | macOS-Viewer |
|---|---|---|
| Sprache / Runtime | C# / .NET 8 | Swift 5.9 |
| UI | WPF | SwiftUI |
| Capture | `Windows.Graphics.Capture` | — |
| Encode / Decode | Media Foundation H.264 (HW) | VideoToolbox H.264 (HW) |
| Rendering | — | Metal via `RTCMTLNSVideoView` |
| Input | `SendInput` (Injektion) | `NSEvent` + `CGEventTap` (Erfassung) |
| Transport | SIPSorcery WebRTC | Google `WebRTC.xcframework` |
| Krypto | NSec (libsodium) | swift-crypto |
| Signaling | Node 20+ / TypeScript / `ws` — plattformneutral |
