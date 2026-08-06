# 7 — Offene Punkte, Alternativen und Reihenfolge

## 7.1 Was zum Laufen fehlt

Vier markierte Stellen trennen dieses Gerüst von einer funktionierenden
Anwendung. Jede trägt einen schrittweisen Leitfaden im Quelltext.

| # | Stelle | Aufwand | Blockiert |
|---|---|---|---|
| 1 | `Capture/D3D11Helper.CreateDevice()` | ~1 h | **Alles.** Ohne D3D-Device startet der Host nicht. |
| 2 | `Encoding/MediaFoundationH264Encoder` | ~1–2 Tage | Den Videostrom. Ohne ihn steht die Verbindung, aber es kommt kein Bild an. |
| 3 | `Capture/WindowThumbnailProvider` | ~4 h | Nur die Live-Vorschau im App-Picker. Auswahl funktioniert auch mit Platzhalter-Kacheln. |
| 4 | Cursorform im Viewer | ~2 h | Nichts. Reines Komfort-Feature. |

**Empfohlene Reihenfolge:** 1 → 2 → 3 → 4. Nach Schritt 2 ist das System
vollständig benutzbar; 3 und 4 sind Politur.

Zu Schritt 2, weil er der größte ist: Der Leitfaden in
`MediaFoundationH264Encoder.cs` ist in sieben Schritten ausgeführt. Der
kritischste ist Schritt 2 (D3D-Device-Manager an den MFT übergeben) — ohne ihn
läuft alles über den Systemspeicher, und die gesamte Zero-Copy-Architektur aus
`CaptureEngine` ist wirkungslos. Der Unterschied ist etwa Faktor 3 in der
CPU-Last bei 1080p60.

---

## 7.2 Sinnvolle Erweiterungen

Nach Priorität, mit dem jeweiligen Grund:

### Integrationstests für Gate 3

Das wichtigste Gate ist derzeit nur von Hand testbar (siehe
[`06-build-and-run.md` §6.6](06-build-and-run.md)). Ein Test, der ein echtes
Fenster erzeugt, es in den Vordergrund bringt, Eingaben injiziert, dann ein
anderes Fenster aktiviert und prüft, dass nichts mehr ankommt — das ist die
Zusicherung, auf der App-Freigabe beruht, und sie sollte automatisch geprüft
werden.

### Ratenbegrenzung für Eingaben

`InputGuard.cs` trägt dafür ein markiertes TODO. Ein bösartiger Viewer kann den
Host derzeit mit Events fluten; die Gates halten sie im Scope, aber die CPU-Last
entsteht. Ein Token-Bucket mit ~500 Events/s Dauerrate und 2000 Burst fängt das
ab, ohne legitime schnelle Mausbewegungen (60–125 Hz) zu stören.

### QR-Code für das Pairing-Ticket

Ein 77-Zeichen-Code ist die unangenehmste Stelle der Bedienung. Ein QR-Code im
Host-Dashboard, den der Viewer mit der Kamera abfotografiert (macOS:
`VNDetectBarcodesRequest`), eliminiert Tippfehler vollständig. Kleiner Aufwand,
große Wirkung.

### Zwischenablage mit eigenem Consent

Text zwischen den Rechnern zu kopieren ist die häufigste Anfrage bei solchen
Werkzeugen. Wichtig: **eigener Kanal, eigene Zustimmung, eigene Anzeige im
Overlay.** Die Zwischenablage still an den Input-Stream zu hängen wäre genau die
Art von unsichtbarer Datenübertragung, gegen die dieses Projekt gebaut ist.

### Audio

Der Medienpfad ist so gebaut, dass ein zweiter Track ohne Umbau dazukommt.
WASAPI-Loopback auf der Host-Seite, Opus-Kodierung, zweiter WebRTC-Track. Der
Consent-Dialog braucht eine dritte Option und das Overlay ein Mikrofon-Symbol —
Audio zu übertragen, ohne dass es angezeigt wird, wäre ein Rückschritt.

### Desktop-Duplication-Fallback

Für Windows-Versionen vor 1803 und für Vollbild-Freigabe generell. DXGI Desktop
Duplication ist ähnlich schnell wie WGC und hat weniger Overhead — kann aber
grundsätzlich kein einzelnes Fenster erfassen, weshalb es nur ein Fallback ist.

### CPace für kurze Pairing-Codes

Der schönste Fortschritt bei der Bedienung: „123456" statt 77 Zeichen. Braucht
Ristretto255-Gruppenoperationen, die libsodium bietet, NSec aber nicht exponiert
— also P/Invoke direkt auf `crypto_core_ristretto255_from_hash` und
`crypto_scalarmult_ristretto255`. `Security/Handshake.cs` ist so angelegt, dass
das Schema im `hello` aushandelbar wäre. Erst angehen, wenn jemand mit
Krypto-Erfahrung mitliest — siehe [`05-security.md` §5.2](05-security.md) zur
Begründung, warum es nicht der Standardweg ist.

---

## 7.3 Entscheidungen im Rückblick

Die drei Entscheidungen, die am ehesten anders ausfallen könnten:

### WebRTC statt QUIC

WebRTC wurde gewählt, weil NAT-Traversal das eigentlich schwierige Problem ist
und ICE die einzige ausgereifte Lösung dafür. **Anders wäre richtig, wenn** beide
Rechner über ein VPN oder Tailscale erreichbar wären — dann entfällt der ganze
ICE-Aufwand, und ein eigenes Protokoll über QUIC wäre schlanker, latenzärmer und
deutlich einfacher zu debuggen. QUIC-Datagramme sind für Input-Events geradezu
ideal.

### Langer Einmal-Code statt PAKE

Siehe oben und [`05-security.md` §5.2](05-security.md). **Anders wäre richtig,
wenn** das Projekt viele Nutzer bekäme — dann rechtfertigt die Bedienung den
Aufwand einer geprüften CPace-Implementierung.

### WPF statt WinUI 3 oder Rust

WPF gewinnt bei Overlay-Fensterverwaltung, Tray-Icon und einfacher Verteilung —
genau den drei Dingen, die AppControl braucht. **Anders wäre richtig, wenn** der
Capture- und Encoder-Kern in Rust mit `windows-rs` neu geschrieben würde; das
wäre ein sinnvoller späterer Schritt, weil diese Schicht ohnehin dünner Interop
über nativem Code ist und dort am meisten Performance liegt. Die UI-Schicht sollte
dann trotzdem verwaltet bleiben.

---

## 7.4 Was nicht kommen sollte

Zur Vollständigkeit, weil diese Punkte bei Werkzeugen dieser Art regelmäßig
vorgeschlagen werden:

| Vorschlag | Warum nicht |
|---|---|
| **Unattended Access** | Der Unterschied zwischen Screen-Sharing und einem Remote-Access-Trojaner ist nicht die Technik — es ist, ob die Person am Gerät weiß, was passiert. Ein Modus ohne Anwesenden löscht genau diesen Unterschied. |
| **Autostart / Dienstbetrieb** | Dasselbe. `App.xaml.cs` lehnt nicht-interaktive Sitzungen aktiv ab. |
| **„Diesem Gerät immer vertrauen"** | Vertrauen in einen Schlüssel heißt „ich weiß, wer anfragt" — nicht „er darf jederzeit". Der Consent-Dialog bei jeder Sitzung ist der Kern des Modells, nicht Reibung, die man wegoptimiert. |
| **Overlay ausblendbar machen** | Der Watchdog macht das Fehlen des Overlays zum Abbruchgrund. Eine Option dagegen wäre keine Einstellung, sondern eine andere Anwendung. |
| **Mehrere gleichzeitige Viewer** | Vergrößert die Angriffsfläche des Consent-Modells erheblich für einen Anwendungsfall, den es hier nicht gibt. |
| **Dateiübertragung im selben Kanal** | Eigenständiger Vertrauens- und Malware-Vektor. Wenn, dann mit eigenem Consent und eigener Anzeige. |

Wer dieses Projekt forkt, kann all das einbauen — so funktioniert Software. Die
Absicht der Architektur ist, dass es **auffallen würde**: Diese Eigenschaften
sind nicht über den Code verstreut, sondern in `SessionStateMachine`,
`StatusOverlayWindow`, `InputGuard` und `App.xaml.cs` konzentriert, und
`tools/consistency/check.py` prüft sie automatisch. Ein Diff, der Transparenz
entfernt, ist ein kurzer und sehr gut sichtbarer Diff.
