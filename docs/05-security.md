# 5 — Netzwerk & Sicherheit

## 5.1 Bedrohungsmodell

**Was geschützt wird:** Bildschirminhalte (potenziell Passwörter, private Nachrichten, Code) und
die Fähigkeit, Eingaben auf einem fremden Rechner auszulösen. Beides ist hochsensibel — die
Eingabekontrolle sogar mehr als der Bildinhalt, weil sie Zustandsänderungen erlaubt.

**Angreifer, gegen die verteidigt wird:**

| # | Angreifer | Fähigkeiten | Abwehr |
|---|---|---|---|
| A1 | Passiver Netzwerk-Mitleser | liest allen Verkehr (WLAN, ISP, Backbone) | Alles verschlüsselt: Signaling per ChaCha20-Poly1305, Medien per DTLS-SRTP |
| A2 | **Bösartiger Signaling-Server** | sieht und verändert alles Signaling | Payloads sind E2E-verschlüsselt; DTLS-Fingerprints reisen darin; SAS-Verifikation deckt Manipulation auf |
| A3 | Bösartiges TURN-Relay | sieht und verändert alle Medienpakete | SRTP wird zwischen den Endpunkten ausgehandelt; das Relay sieht nur Ciphertext |
| A4 | Fremder mit der Raum-ID | kennt `roomId`, nicht aber `psk` | Handshake schlägt fehl (AEAD-Authentifizierung); zusätzlich Consent-Dialog |
| A5 | Legitimer Viewer, der Grenzen überschreitet | gültige Session, versucht außerhalb des Scopes zu klicken | Die fünf Input-Gates (Kapitel 3.7) |
| A6 | Kompromittierter Viewer-Rechner | volle Kontrolle über die Viewer-Seite | Host-Gates + jederzeitiger Kill-Switch begrenzen den Schaden; **nicht** vollständig abwehrbar |

**Was ausdrücklich außerhalb liegt:**

- Ein kompromittierter **Host**-Rechner. Wer dort Code ausführt, braucht AppControl nicht.
- Ein kompromittierter **Viewer**-Rechner während einer freigegebenen Sitzung mit Steuerung. Der
  Viewer hat dann per Definition die Rechte, die der Host erteilt hat. Die Gates begrenzen den
  Scope, der Kill-Switch die Dauer — mehr kann Software an dieser Stelle nicht leisten.
- Physischer Zugriff, Malware, kompromittierte Betriebssysteme, Seitenkanäle.
- Traffic-Analyse: Der Signaling-Server weiß, dass zwei IP-Adressen zur selben Raum-ID gehören.
  Wer das verbergen will, führt den Server über Tor oder in einem eigenen Netz aus.

---

## 5.2 Das Pairing-Ticket

Der Host erzeugt einmalig pro Sitzung (oder einmalig pro Peer, je nach Modus):

```
roomId  = 16 zufällige Bytes    → öffentlich, der Server sieht sie
psk     = 32 zufällige Bytes    → geheim, der Server sieht sie NIE
```

Anzeige als Crockford-Base32 mit Prüfsumme, gruppiert für die Übertragung per Sprache oder Chat:

```
AC1-4KP7-2NM9-XQR3-8TWV-6HJ5-BZY2-9FD4-1CGN-7SXA-3RPE-VMKT-...
     └────────────── roomId (16 B) ─────────┘└─────── psk (32 B) ──────┘
```

Zusätzlich als **QR-Code** im Host-Dashboard — der Viewer kann ihn mit der Kamera abfotografieren
(macOS: Live Text / `VNDetectBarcodesRequest`), was Tippfehler vollständig eliminiert.

**Übertragungsweg:** Signal, iMessage, WhatsApp, Telefon, QR-Code vor der Kamera. Wichtig ist nur,
dass es **nicht über den Signaling-Server** läuft. Die App weist im UI darauf hin.

### Warum ein langer Code statt „123456"?

Ein sechsstelliger Code hat ~20 Bit Entropie. Wird er zum Ableiten eines Verschlüsselungsschlüssels
verwendet, kann ein Angreifer, der einen einzigen Handshake-Umschlag mitliest, **offline** alle
10⁶ Möglichkeiten durchprobieren — Sekunden auf einem Laptop. Kurze Codes sind nur mit einem
**PAKE** (Password-Authenticated Key Exchange) sicher, bei dem jeder Rateversuch eine neue
Online-Interaktion erfordert.

Ein PAKE wäre die schönere UX. Er ist hier aus zwei Gründen nicht der Standardweg:

1. Weder `NSec` noch `swift-crypto` bieten die nötigen Gruppenoperationen (Hash-to-Curve,
   Punkt-Addition auf Ristretto255) an. Man müsste sie selbst implementieren.
2. Selbstgeschriebene PAKE-Implementierungen sind eine bekannte Quelle subtiler, katastrophaler
   Fehler — invalid-curve-Angriffe, fehlende Kofaktor-Behandlung, Timing-Lecks.

Da die beiden Nutzer ohnehin einen sicheren Kanal haben (sie sind befreundet und schreiben
miteinander), ist ein 256-Bit-Einmalcode über diesen Kanal die schlichtere und stärkere Lösung.

**Migrationspfad zu kurzen Codes:** `Security/PairingScheme.cs` bzw. `.swift` ist als Interface
angelegt. Eine CPace-Implementierung auf Basis von `libsodium`s Ristretto255-Funktionen
(`crypto_core_ristretto255_from_hash`, `crypto_scalarmult_ristretto255`) — die in libsodium
vorhanden, in NSec aber nicht exponiert sind, also über P/Invoke erreichbar — würde als zweite
Implementierung danebengelegt. Das Protokoll handelt das Schema im `hello` aus.

---

## 5.3 Der Handshake

Beide Seiten besitzen ein **statisches X25519-Schlüsselpaar** (Langzeit-Identität), erzeugt beim
ersten Start und gespeichert in der Windows-Credential-Manager-/macOS-Keychain (siehe 5.5).

```
Voraussetzung: beide kennen psk (aus dem Ticket)
k_hs = HKDF-SHA256(ikm = psk, salt = roomId, info = "ac/1 handshake", len = 32)

Host  → Viewer:  AEAD_k_hs{ e_H, s_H, name_H, nonce_H }
Viewer → Host:   AEAD_k_hs{ e_V, s_V, name_V, nonce_V }

dh_1 = X25519(e_eigen, e_fremd)      # Ephemeral × Ephemeral
dh_2 = X25519(e_H,     s_V)          # Host-Ephemeral × Viewer-Static
dh_3 = X25519(s_H,     e_V)          # Host-Static × Viewer-Ephemeral
       (beide Seiten berechnen dieselben drei Werte, jeweils mit ihrem eigenen privaten Schlüssel)

session_key = HKDF-SHA256(ikm  = dh_1 ‖ dh_2 ‖ dh_3,
                          salt = SHA256(nonce_H ‖ nonce_V),
                          info = "ac/1 session", len = 32)
```

Das ist die Struktur von **Noise `KK`** mit einem vorgeschalteten PSK-Umschlag. Die drei
Diffie-Hellman-Operationen leisten zusammen:

- **`dh_1`** — Forward Secrecy. Werden später beide statischen Schlüssel kompromittiert, bleiben
  aufgezeichnete Sitzungen unlesbar, weil die ephemeren Schlüssel nach der Sitzung gelöscht werden.
- **`dh_2`** — bindet die Sitzung an die statische Identität des Viewers. Nur wer `s_V` besitzt,
  kann den Schlüssel berechnen.
- **`dh_3`** — bindet sie an die des Hosts. Beidseitige Authentifizierung.

Der PSK-Umschlag darüber verhindert, dass ein Angreifer, der nur `roomId` kennt, überhaupt am
Handshake teilnehmen kann.

### Warum X25519 statt Ed25519 für die Identität?

X25519 kann nicht signieren, aber wir brauchen keine Signaturen: Die Authentifizierung entsteht
aus den DH-Operationen selbst („nur wer den privaten Schlüssel hat, kommt auf denselben
Sitzungsschlüssel"). Das ist weniger Code und weniger Möglichkeit, etwas falsch zu machen, als ein
Signaturschema mit eigener Nachrichtenzusammensetzung. `s_static` ist ein reiner
Schlüsselaustausch-Schlüssel und wird nie für etwas anderes verwendet — keine Doppelnutzung
desselben Schlüsselmaterials für zwei Zwecke.

---

## 5.4 SAS — die menschliche Verifikation

```
sas = HKDF-SHA256(session_key, info = "ac/1 sas", len = 5)
→ 5 Emojis aus einer 64er-Tabelle = 30 Bit
```

Beide Seiten zeigen dieselben fünf Emojis. Die Nutzer vergleichen sie über einen **anderen** Kanal
(Telefon, Videocall, Chat). Stimmen sie überein, ist ausgeschlossen, dass sich jemand
dazwischengeschaltet hat — ein MitM müsste zwei verschiedene Sitzungsschlüssel etablieren, und die
Wahrscheinlichkeit, dass beide dieselben 5 Emojis ergeben, liegt bei 1 : 2³⁰ (~1 : 1,07 Mrd.).

Emojis statt Hex-Ziffern, weil Menschen sie zuverlässiger vergleichen: `🐙🍒🚀🔔🎩` fällt sofort
auf, wenn eines abweicht; `4f2a9c81e07b` gleicht niemand ernsthaft ab. Der Ansatz ist von Signals
Safety Numbers und Magic Wormhole übernommen.

Die Tabelle steht in [`protocol/emoji-table.md`](../protocol/emoji-table.md) und ist auf visuell
und sprachlich gut unterscheidbare Symbole beschränkt — keine Hautfarben-Modifier, keine
Flaggen, keine Emojis, die sich zwischen Plattformen stark unterscheiden.

---

## 5.5 Trust On First Use (TOFU)

Nach erfolgreichem Handshake speichert jede Seite den statischen Schlüssel der Gegenseite:

```jsonc
// %LOCALAPPDATA%\AppControl\peers.json  bzw.  ~/Library/Application Support/AppControl/peers.json
{
  "peers": [{
    "staticKey":  "<base64 32B>",
    "name":       "Jakobs MacBook Pro",
    "fingerprint":"4f2a 9c81 3b7e 0d45 … e07b",
    "firstSeen":  "2026-08-06T14:22:00Z",
    "lastSeen":   "2026-08-14T09:03:00Z",
    "sessions":   12
  }]
}
```

Ab dem zweiten Mal:

- **Bekannter Schlüssel** → Consent-Dialog zeigt `[✓ bekannt seit 6. August, 12 Sitzungen]`.
  Ein Ticket wird trotzdem gebraucht, oder es kann ein **Direct-Reconnect** ohne neues Ticket
  genutzt werden (dann leitet sich `k_hs` aus dem gespeicherten statischen Schlüssel ab).
- **Neuer Schlüssel unter bekanntem Namen** → **rote Warnung**, Text ohne Beschönigung:
  „Dieser Rechner meldet sich als ‚Jakobs MacBook Pro', hat aber einen anderen Schlüssel als
  zuvor. Das passiert bei einer Neuinstallation — oder bei einem Angriff. Frag nach, bevor du
  freigibst." Der Dialog verlangt in diesem Fall zusätzlich das explizite Abhaken des SAS-Vergleichs.

**Speicherung der privaten Schlüssel:**

| Plattform | Mechanismus |
|---|---|
| Windows | DPAPI (`ProtectedData.Protect`, `CurrentUser`-Scope) — an Benutzerkonto gebunden |
| macOS | Keychain, `kSecAttrAccessibleWhenUnlockedThisDeviceOnly` — kein iCloud-Sync |

Beides bedeutet: Der Schlüssel verlässt das Gerät nicht und ist ohne Anmeldung des Benutzers nicht
lesbar.

---

## 5.6 Verbindungsaufbau: P2P und Fallback

ICE probiert der Reihe nach:

| Priorität | Typ | Beschreibung | Anteil in der Praxis |
|---|---|---|---|
| 1 | `host` | direkte lokale IP — funktioniert im selben LAN | ~5 % |
| 2 | `srflx` | STUN-reflexive Adresse — Hole-Punching durch NAT | ~80 % |
| 3 | `relay` | TURN-Relay | ~15 % |

STUN-Server sind kostenlos (`stun:stun.l.google.com:19302` als Default; für Unabhängigkeit besser
der eigene coturn). TURN ist der Fallback für symmetrische NATs und restriktive Firmennetze und
verursacht Traffic-Kosten — deshalb schickt der Signaling-Server **kurzlebige TURN-Credentials**
(HMAC-basiert nach REST-API-Draft, 10 min gültig), statt statische Zugangsdaten in der App zu
hinterlegen.

**Auch über TURN bleibt der Inhalt geschützt:** Das Relay leitet SRTP-Pakete weiter, deren
Schlüssel aus dem DTLS-Handshake zwischen Host und Viewer stammen. Es sieht Ciphertext, Zeitpunkte
und Paketgrößen — mehr nicht.

Die Viewer-UI zeigt den tatsächlich genutzten Pfad an (`🔗 Direkt (P2P)` vs. `🔁 Über Relay`),
damit erkennbar ist, ob Traffic über Dritte läuft.

---

## 5.7 Verschlüsselung im Überblick

```
┌──── Signaling (WebSocket) ──────────────────────────────────────────┐
│  TLS 1.3 zum Server                            (Transportsicherheit)│
│  └── ChaCha20-Poly1305 mit psk / session_key   (E2E — Server blind) │
└─────────────────────────────────────────────────────────────────────┘

┌──── Medien + DataChannels (WebRTC) ─────────────────────────────────┐
│  DTLS 1.2 Handshake  → Fingerprint im E2E-Umschlag verifiziert      │
│  ├── SRTP (AES-128-GCM)  : Videostrom                               │
│  └── DTLS/SCTP           : ac-control, ac-input, ac-cursor          │
└─────────────────────────────────────────────────────────────────────┘
```

Zwei unabhängige Schichten. Selbst wenn der TLS-Kanal zum Signaling-Server bricht (kompromittierte
CA, MitM-Proxy), bleibt der Inhalt geschützt. Und selbst wenn der E2E-Handshake fehlerhaft wäre,
liegt darunter noch DTLS-SRTP. Ein Angreifer muss beide Schichten brechen.

---

## 5.8 Was der Signaling-Server sieht — und nicht sieht

| Sieht er | Sieht er nicht |
|---|---|
| IP-Adressen beider Teilnehmer | Bildschirminhalte |
| `roomId` (zufällig, ohne Bedeutung) | Eingaben |
| Zeitpunkt und Dauer der Verbindung | Namen der Teilnehmer oder Geräte |
| Größe der weitergeleiteten Umschläge | SDP, ICE-Kandidaten, IP-Kandidaten |
| — | statische Schlüssel, `psk`, `session_key` |
| — | ob überhaupt eine Sitzung zustande kam |

Der Server ist ~300 Zeilen ohne Datenbank und ohne Logging von Payloads. Er lässt sich für ~3 €/Monat
selbst betreiben; die Anleitung steht in [`06-build-and-run.md`](06-build-and-run.md).

---

## 5.9 Bekannte Schwächen und offene Punkte

Ehrlich benannt, weil ein Sicherheitskapitel ohne diesen Abschnitt unvollständig ist:

1. **SAS-Verifikation ist optional erzwingbar, nicht technisch erzwungen.** Ein Nutzer kann
   „stimmt überein" anklicken, ohne verglichen zu haben. Der Dialog gestaltet das so unbequem wie
   vertretbar (eigene Checkbox, keine Vorauswahl), aber am Ende ist es eine menschliche Handlung.
2. **`psk` im Klartext im Chat.** Wird das Ticket über einen kompromittierten Chat verschickt,
   kann ein Angreifer den Handshake übernehmen. Dagegen hilft nur die SAS-Verifikation — und dass
   der Host trotzdem noch bewusst zustimmen muss.
3. **Kein Schutz gegen einen kompromittierten Viewer.** Siehe Bedrohungsmodell.
4. **Keine formale Verifikation des Handshakes.** Die Konstruktion folgt Noise `KK`, ist aber nicht
   mit einem Model-Checker (Tamarin/ProVerif) geprüft. Für ein Werkzeug dieser Größe angemessen,
   für ein Produkt mit vielen Nutzern wäre es der nächste Schritt.
5. **Metadaten-Leck zum Signaling-Server.** Siehe 5.8.
6. **Kein Rate-Limit auf Input-Events.** Ein bösartiger Viewer kann den Host mit Events fluten.
   Die Gates halten sie im Scope, aber die CPU-Last entsteht. `Input/InputGuard.cs` enthält einen
   markierten Platz für ein Token-Bucket-Limit — bewusst als sichtbares TODO, nicht als stille Lücke.
