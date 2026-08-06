# AppControl

Bildschirmfreigabe mit Fernsteuerung zwischen einem **Windows-Host** und einem
**macOS-Viewer** — gebaut um die Frage herum, wie man verhindert, dass so etwas
zu einem Fernwartungstrojaner wird.

```
┌────────────────────────┐        ┌───────────┐        ┌───────────────────────┐
│  🪟  Windows — Host    │        │ Signaling │        │  🍎  macOS — Viewer   │
│                        │───────▶│ (blind)   │◀───────│                       │
│  wählt, was geteilt    │        └───────────┘        │  sieht zu, steuert    │
│  wird · sieht es immer │                             │  nach Freigabe        │
│  · stoppt mit 1 Klick  │◀═══ P2P · DTLS-SRTP ═══════▶│                       │
└────────────────────────┘                             └───────────────────────┘
```

---

## Die Idee in vier Sätzen

Der Host wählt aktiv aus, was geteilt wird — der ganze Bildschirm oder eine
einzelne Anwendung. Er sieht permanent in einem nicht schließbaren Overlay, dass
geteilt wird, *was* geteilt wird und ob gerade jemand steuert. Er kann mit einem
Klick oder einem Hotkey sofort abbrechen. Ohne seine explizite Zustimmung im
Consent-Dialog verlässt kein einziges Frame den Rechner.

Der Viewer bekommt einen H.264-Stream mit niedriger Latenz und darf — **nach
separater Freigabe** — Maus und Tastatur schicken.

---

## Was dieses Projekt anders macht

Die Technik hinter Screen-Sharing und hinter einem Remote-Access-Trojaner ist
dieselbe. Der Unterschied ist, ob die Person am Gerät weiß, was passiert. Das ist
hier keine Randbedingung, sondern die Architektur:

| Mechanismus | Umsetzung |
|---|---|
| **Eine Wahrheitsquelle** | `SessionStateMachine` entscheidet alles. Capture und Input halten kein eigenes Zustandsflag — sie fragen sie bei *jedem* Frame und *jedem* Event. Ein Zustand „streamt, aber Overlay zeigt was anderes" ist strukturell unmöglich. |
| **Fünf Input-Gates** | Zustand · Steuerungsfreigabe · Fenster im Vordergrund · Geometrie · Tasten-Blocklist. Alle Deny-by-default, alle auf dem Host, keines vom Viewer beeinflussbar. |
| **Overlay als Vorbedingung** | Ein Watchdog stellt es wieder her — und **beendet die Sitzung**, wenn das dreimal misslingt. Es ist keine Anzeige, die man wegklicken kann. |
| **Gelber Systemrahmen bleibt an** | `IsBorderRequired` wird nie auf `false` gesetzt, die Capability `graphicsCaptureWithoutBorder` nie angefordert. Ein vom Betriebssystem garantierter Hinweis, den nicht mal ein kompromittierter AppControl-Prozess abschalten könnte. |
| **Kein Stealth-Modus** | Kein Autostart, kein `--silent`, kein Dienstbetrieb, kein „immer vertrauen". `App.xaml.cs` lehnt nicht-interaktive Sitzungen aktiv ab. |
| **Not-Aus, doppelt** | `Strg+Alt+Umschalt+X` **und** dreimal rechte Strg. Ein Low-Level-Hook prüft `LLKHF_INJECTED` — der Viewer kann ihn weder auslösen noch stören. Gehaltene Tasten werden garantiert freigegeben. |
| **Server sieht nichts** | Signaling und TURN transportieren nur Ciphertext. Die Schlüssel entstehen ausschließlich zwischen Host und Viewer. |

Diese Eigenschaften sind nicht über den Code verstreut, sondern in vier Dateien
konzentriert — und `tools/consistency/check.py` prüft sie automatisch. Ein Commit,
der eine davon entfernt, wird rot.

---

## Tech-Stack

| Schicht | Windows-Host | macOS-Viewer |
|---|---|---|
| Sprache | C# / .NET 8 | Swift 5.9 |
| UI | WPF | SwiftUI |
| Capture | `Windows.Graphics.Capture` | — |
| Codec | Media Foundation H.264 (HW) | VideoToolbox H.264 (HW) |
| Rendering | — | Metal via `RTCMTLNSVideoView` |
| Input | `SendInput` | `NSEvent` + `CGEventTap` |
| Transport | SIPSorcery WebRTC | Google `WebRTC.xcframework` |
| Krypto | NSec (libsodium) | swift-crypto |
| Signaling | Node 20+ / TypeScript — plattformneutral |

Begründung jeder Entscheidung und die verworfenen Alternativen:
[`docs/02-tech-stack.md`](docs/02-tech-stack.md).

---

## Sicherheit in Kurzform

- **Pairing:** 256-Bit-Einmalcode, über einen bestehenden sicheren Kanal (Signal,
  iMessage, Telefon) übermittelt — nie über den Signaling-Server
- **Handshake:** Triple-DH über X25519 (Noise-`KK`-Struktur) mit
  PSK-Umschlag → Forward Secrecy + beidseitige Authentifizierung
- **SAS:** fünf Emojis (30 Bit), auf beiden Seiten angezeigt, per Telefon
  verglichen → Man-in-the-Middle fliegt auf
- **Signaling:** ChaCha20-Poly1305 Ende-zu-Ende, mit Replay- und
  Richtungsprüfung — der Server sieht nur Ciphertext
- **Medien:** DTLS-SRTP, Fingerprint innerhalb des E2E-Umschlags → auch ein
  bösartiger Server kann sich nicht dazwischenschalten
- **Vertrauen:** TOFU mit Schlüssel-Pinning; ein geänderter Schlüssel unter
  bekanntem Namen erzeugt eine rote Warnung und macht den SAS-Vergleich zur Pflicht

Bedrohungsmodell und die ehrlich benannten Schwächen:
[`docs/05-security.md`](docs/05-security.md).

---

## Dokumentation

| Kapitel | Inhalt |
|---|---|
| [01 — Architektur](docs/01-architecture.md) | Komponenten, Datenflüsse, Zustandsmaschine |
| [02 — Tech-Stack](docs/02-tech-stack.md) | Jede Entscheidung mit Begründung und verworfenen Alternativen |
| [03 — Consent & Transparenz](docs/03-consent-and-transparency.md) | **Die eigentliche Spezifikation** |
| [04 — Protokoll](docs/04-protocol.md) | Alle Nachrichten, Byte-genau |
| [05 — Sicherheit](docs/05-security.md) | Bedrohungsmodell, Handshake, Schwächen |
| [06 — Build & Test](docs/06-build-and-run.md) | Setup beider Plattformen, manuelle Testfälle |
| [07 — Roadmap](docs/07-roadmap.md) | Was fehlt, was kommen sollte, was nicht |

---

## Schnellstart

```bash
# 1) Signaling-Server
cd signaling && npm install && npm run build && npm start

# 2) Host (auf Windows)
cd host-windows && dotnet build -c Release
dotnet run --project src/AppControl.Host

# 3) Viewer (auf macOS)
cd viewer-macos && swift build -c release && .build/release/AppControlViewer
```

Schritt für Schritt auf beiden Rechnern: [`SETUP.md`](SETUP.md).
Ausführlich, inklusive TLS-Deployment und TURN:
[`docs/06-build-and-run.md`](docs/06-build-and-run.md).

---

## Prüfungen

```bash
cd signaling && npm test                    # 27 Tests — Räume, Protokoll, E2E
node tools/crypto-vectors/verify.mjs        # 10 Prüfungen — Handshake, AEAD
python3 tools/consistency/check.py          # 52 Prüfungen — C# ↔ Swift ↔ JS
cd host-windows && dotnet test              # Krypto, Gates, Decoder, HID
cd viewer-macos && swift test               # Krypto, Encoder, HID
```

Der Konsistenz-Check ist der ungewöhnlichste: Er vergleicht Emoji-Tabellen,
Nachrichtentyp-Bytes, Header-Layouts und HID-Abbildungen zwischen den drei
Implementierungen — und prüft zusätzlich die Transparenz-Zusicherungen aus
Kapitel 3.

---

## Stand

| Komponente | Zustand |
|---|---|
| Signaling-Server | ✅ vollständig, 27 Tests grün |
| Krypto-Referenz + Vektoren | ✅ vollständig, 10 Prüfungen grün |
| Konsistenz-Check | ✅ vollständig, 52 Prüfungen grün |
| Host: Zustand, Gates, Krypto, UI, Protokoll | ✅ ausgeführt |
| Host: D3D-Device, H.264-Encoder, Live-Vorschau | 🔨 Leitfaden im Quelltext, siehe [Roadmap](docs/07-roadmap.md) |
| Viewer | ✅ ausgeführt bis auf Cursorform-Übernahme |

Die offenen Stellen liegen bewusst dort, wo der Code reine Interop-Mechanik ist
(Media Foundation, D3D11) — nicht in der Logik, die AppControl ausmacht. Jede
trägt einen schrittweisen Leitfaden im Kommentar.

---

## Lizenz

MIT — siehe [LICENSE](LICENSE).
