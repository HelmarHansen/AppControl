# 1 — Architekturüberblick

> AppControl ist ein Zwei-Parteien-Remote-Control-Tool: **Host** (Windows, teilt Bildschirm oder
> eine einzelne App) und **Viewer** (macOS, sieht zu und darf — nach expliziter Freigabe — Maus und
> Tastatur steuern). Es ist bewusst *kein* Mehrbenutzer-, kein Unattended- und kein
> Enterprise-Fernwartungsprodukt. Diese Beschränkung ist eine Sicherheitsentscheidung, keine Lücke.

---

## 1.1 Leitplanken der Architektur

Vier Eigenschaften bestimmen jede Designentscheidung in diesem Projekt, in dieser Reihenfolge:

| # | Leitplanke | Konsequenz |
|---|---|---|
| 1 | **Der Host behält die Kontrolle** | Kein Frame verlässt den Rechner, bevor der Host zugestimmt hat. Kein Input wird injiziert, bevor der Host Steuerung *separat* freigegeben hat. Stoppen ist immer ein Klick / ein Hotkey. |
| 2 | **Nichts passiert unsichtbar** | Es gibt keinen Stealth-Modus, keinen Headless-Betrieb, keinen Autostart. Die App verweigert den Start ohne sichtbare UI. Ein Overlay zeigt permanent den Zustand. |
| 3 | **Der Server ist nicht vertrauenswürdig** | Signaling- und TURN-Server sehen nur Ciphertext. Die Ende-zu-Ende-Schlüssel entstehen ausschließlich zwischen Host und Viewer. |
| 4 | **Latenz vor Bildqualität** | Bei Konflikt gewinnt die Reaktionszeit: kleinere Auflösung, mehr Kompression, verworfene Frames — aber niemals eine wachsende Warteschlange. |

---

## 1.2 Komponenten in der Vogelperspektive

```mermaid
graph TB
    subgraph WIN["🪟 Windows — Host (Freund)"]
        direction TB
        HUI["Host-UI (WPF)<br/>Dashboard · App-Picker · Consent-Dialog"]
        OVL["Status-Overlay<br/>always-on-top, nicht schließbar"]
        TRAY["Tray-Icon<br/>Zustandsfarbe + Menü"]
        HOTK["Emergency-Hotkey<br/>Low-Level-Keyboard-Hook"]
        SM["SessionStateMachine<br/>zentrale Wahrheit über den Zustand"]
        CAP["CaptureEngine<br/>Windows.Graphics.Capture"]
        ENC["VideoEncoder<br/>Media Foundation H.264 (HW)"]
        INJ["InputInjector<br/>SendInput + InputGuard"]
        AUD["AuditLog<br/>append-only, lokal"]
        HNET["PeerConnection (SIPSorcery)<br/>WebRTC: 1 Video-Track + 3 DataChannels"]
    end

    subgraph CLOUD["☁️ Nicht vertrauenswürdige Infrastruktur"]
        SIG["Signaling-Server<br/>WebSocket-Rendezvous<br/><i>sieht nur Ciphertext</i>"]
        TURN["TURN-Relay (coturn)<br/><i>sieht nur SRTP-Ciphertext</i>"]
    end

    subgraph MAC["🍎 macOS — Viewer (du)"]
        direction TB
        VUI["Viewer-UI (SwiftUI)<br/>Pairing · SAS-Check · Session-Fenster"]
        REN["VideoRenderer<br/>RTCMTLNSVideoView / VideoToolbox"]
        ICAP["InputCapture<br/>NSEvent + CGEventTap"]
        VNET["PeerConnection (WebRTC.framework)"]
    end

    HUI --> SM
    OVL --> SM
    TRAY --> SM
    HOTK -->|"Kill-Switch"| SM
    SM -->|"darf capturen?"| CAP
    SM -->|"darf injizieren?"| INJ
    SM --> AUD
    CAP --> ENC --> HNET
    HNET --> INJ

    HNET <-.->|"1· verschlüsseltes Signaling"| SIG
    VNET <-.->|"1· verschlüsseltes Signaling"| SIG
    HNET <===>|"2· P2P: DTLS-SRTP (Normalfall)"| VNET
    HNET <-.->|"3· Fallback: TURN-Relay"| TURN
    TURN <-.-> VNET

    VNET --> REN --> VUI
    ICAP --> VNET

    classDef host fill:#1e3a5f,stroke:#4a90d9,color:#fff
    classDef mac fill:#3d2f4f,stroke:#a678c4,color:#fff
    classDef cloud fill:#4a3c1e,stroke:#c9a227,color:#fff
    class HUI,OVL,TRAY,HOTK,SM,CAP,ENC,INJ,AUD,HNET host
    class VUI,REN,ICAP,VNET mac
    class SIG,TURN cloud
```

**Zu lesen ist das so:** Die einzige Komponente, die auf dem Host entscheidet, ob etwas passiert,
ist die `SessionStateMachine`. `CaptureEngine` und `InputInjector` fragen sie bei *jedem* Frame und
*jedem* Event. Es gibt keinen Pfad, der an ihr vorbeiführt — auch nicht über das Netzwerk.

---

## 1.3 Die drei Kanäle

WebRTC gibt uns einen Medien-Track und beliebig viele DataChannels über *eine* ausgehandelte
DTLS-Verbindung. Wir nutzen vier logische Kanäle mit bewusst unterschiedlichen
Zuverlässigkeitsgarantien:

| Kanal | Typ | Zuverlässigkeit | Inhalt |
|---|---|---|---|
| `video` | RTP/SRTP Media-Track | lossy, congestion-controlled | H.264-Stream des freigegebenen Inhalts |
| `ac-control` | DataChannel | **reliable, ordered** | Consent, Share-State, Control-State, Stats, Bye |
| `ac-input` | DataChannel | **reliable, ordered** | Tastendrücke, Mausklicks, Scroll — alles, wo ein verlorenes Event einen kaputten Zustand hinterlässt (z. B. "Taste gedrückt" ohne "losgelassen") |
| `ac-cursor` | DataChannel | **unreliable** (`maxRetransmits: 0`) | reine Mausbewegungen — das jüngste Event macht jedes ältere ohnehin irrelevant |

Die Trennung von `ac-input` und `ac-cursor` ist der wichtigste Latenz-Trick des Protokolls: Bei
Paketverlust blockiert ein Retransmit auf einem reliable-ordered Channel *alle* nachfolgenden
Nachrichten (Head-of-Line-Blocking). Mausbewegungen sind mit Abstand die häufigsten Events —
sie über einen unreliablen Kanal zu schicken hält den kritischen Pfad frei.

---

## 1.4 Datenfluss: Verbindungsaufbau

```mermaid
sequenceDiagram
    autonumber
    participant H as Host (Windows)
    participant S as Signaling-Server
    participant V as Viewer (macOS)

    Note over H,V: Phase 0 — Pairing-Ticket (einmalig, außerhalb der App)
    H->>H: Ticket erzeugen: roomId(128 bit) + psk(256 bit)
    H-->>V: Ticket als Base32-String / QR<br/>über Signal, iMessage, Telefon …

    Note over H,V: Phase 1 — Rendezvous
    H->>S: JOIN roomId
    V->>S: JOIN roomId
    S-->>H: peer-joined
    S-->>V: peer-joined

    Note over H,V: Phase 2 — Handshake (Server sieht nur Ciphertext)
    H->>S: ENVELOPE ⟨e_H, statisch_H⟩ AEAD-verschlüsselt mit psk
    S->>V: (weiterleiten)
    V->>S: ENVELOPE ⟨e_V, statisch_V⟩ AEAD-verschlüsselt mit psk
    S->>H: (weiterleiten)
    H->>H: Triple-DH → session_key, SAS-Emoji
    V->>V: Triple-DH → session_key, SAS-Emoji

    Note over H,V: Phase 3 — Menschliche Verifikation
    H->>H: 🖥️ zeigt SAS: 🐙 🍒 🚀 🔔 🎩
    V->>V: 🖥️ zeigt SAS: 🐙 🍒 🚀 🔔 🎩
    Note over H,V: Beide bestätigen, dass die Emojis übereinstimmen

    Note over H,V: Phase 4 — Consent (der eigentliche Gate-Keeper)
    V->>H: consent-request { wantsControl: true }
    H->>H: 🛑 MODALER DIALOG — Standard ist ABLEHNEN
    H->>H: Host wählt: ganzer Bildschirm / einzelne App
    H->>V: consent-response { granted, scope, controlGranted }

    Note over H,V: Phase 5 — Medien
    H->>S: ENVELOPE ⟨SDP-Offer + DTLS-Fingerprint⟩ mit session_key verschlüsselt
    S->>V: (weiterleiten)
    V->>S: ENVELOPE ⟨SDP-Answer + DTLS-Fingerprint⟩
    S->>H: (weiterleiten)
    H<<->>V: ICE-Kandidaten (ebenfalls verschlüsselt)
    H<<->>V: 🔒 DTLS-SRTP steht — direkt P2P
    H->>V: H.264-Frames
    V->>H: Input-Events (nur wenn controlGranted)
```

Der entscheidende Punkt in Phase 5: Der **DTLS-Fingerprint reist innerhalb des mit `session_key`
verschlüsselten Envelopes**. Ein bösartiger Signaling-Server kann ihn nicht durch seinen eigenen
ersetzen, ohne den Schlüssel zu kennen — damit ist ein Man-in-the-Middle auf der Medienstrecke
ausgeschlossen, obwohl der Server jedes Byte durchreicht.

---

## 1.5 Datenfluss: laufender Frame

```mermaid
graph LR
    A["Windows Compositor"] -->|"Direct3D11CaptureFramePool<br/>FrameArrived"| B["CaptureEngine"]
    B -->|"IDirect3DSurface<br/>(bleibt auf der GPU)"| C{"SessionStateMachine<br/>darf gestreamt werden?"}
    C -->|"nein → Frame verwerfen"| X["🗑️"]
    C -->|"ja"| D["FrameGate<br/>Backpressure: max. 1 Frame in flight"]
    D --> E["MediaFoundation<br/>H.264-Encoder (GPU)"]
    E -->|"Annex-B NAL-Units"| F["RTP-Packetizer"]
    F -->|"SRTP"| G(("Netz"))
    G --> H["WebRTC Jitter-Buffer"]
    H --> I["VideoToolbox<br/>H.264-Decoder (HW)"]
    I -->|"CVPixelBuffer"| J["RTCMTLNSVideoView<br/>Metal-Rendering"]

    style C fill:#7a2020,stroke:#ff6b6b,color:#fff
    style D fill:#2d5016,stroke:#8bc34a,color:#fff
```

Zwei Dinge sind hier absichtlich so gebaut:

**Das Gate sitzt vor dem Encoder, nicht vor dem Netzwerk.** Wenn der Host pausiert, wird das Frame
verworfen, *bevor* CPU/GPU-Zeit in die Kompression fließt. Ein pausierter Host kostet praktisch
nichts — und, wichtiger: Es gibt keinen Puffer, in dem noch alte Frames liegen könnten, die nach
dem Pausieren rausgehen.

**Backpressure statt Queue.** Der `FrameGate` lässt maximal ein Frame gleichzeitig im Encoder sein.
Ist der Encoder noch beschäftigt, wenn das nächste Frame kommt, wird das *neue* Frame verworfen —
nicht eingereiht. Eine Queue würde bei Überlast wachsen und die Latenz mit jeder Sekunde
verschlechtern; Verwerfen hält sie konstant. Das ist der Unterschied zwischen "ruckelt bei Last"
und "ist nach zwei Minuten unbenutzbar".

---

## 1.6 Datenfluss: Input (der sicherheitskritische Pfad)

```mermaid
graph TD
    A["macOS: NSEvent / CGEventTap"] --> B["InputCapture<br/>normalisiert auf 0.0–1.0"]
    B --> C["Binärkodierung (12–20 Byte)"]
    C --> D(("ac-input / ac-cursor"))
    D --> E["Host: InputDispatcher"]

    E --> F{"Gate 1<br/>SessionState == Sharing?"}
    F -->|nein| Z["🗑️ verwerfen + zählen"]
    F -->|ja| G{"Gate 2<br/>controlGranted?"}
    G -->|nein| Z
    G --> H{"Gate 3<br/>Ziel-Fenster im Vordergrund?<br/>(nur bei App-Scope)"}
    H -->|nein| Z
    H --> I{"Gate 4<br/>Koordinate im Ziel-Rechteck?"}
    I -->|nein| K["auf Rechteck klemmen"]
    I --> L{"Gate 5<br/>Taste erlaubt?<br/>Blocklist: Win, Ctrl+Alt+Del, Alt+Tab"}
    L -->|nein| Z
    K --> L
    L --> M["SendInput()"]
    M --> N["AuditLog: Zähler++"]

    style F fill:#7a2020,stroke:#ff6b6b,color:#fff
    style G fill:#7a2020,stroke:#ff6b6b,color:#fff
    style H fill:#7a2020,stroke:#ff6b6b,color:#fff
    style I fill:#7a2020,stroke:#ff6b6b,color:#fff
    style L fill:#7a2020,stroke:#ff6b6b,color:#fff
```

Fünf Gates, alle auf dem Host, alle mit **Deny-by-default**. Der Viewer kann keines davon
beeinflussen — er kann nur Events schicken und hoffen. Details und Begründung jedes Gates in
[`03-consent-and-transparency.md`](03-consent-and-transparency.md).

Besonders wichtig ist **Gate 3**: Bei App-Freigabe wird geprüft, ob das freigegebene Fenster gerade
im Vordergrund ist. Wechselt der Host selbst die Anwendung (etwa um ein Passwort einzugeben),
laufen injizierte Events sofort ins Leere. Ohne dieses Gate wäre "ich teile nur Notepad" eine
Illusion — der Viewer könnte mit Mauskoordinaten außerhalb des Fensters in jede beliebige
Anwendung klicken.

---

## 1.7 Zustandsmaschine des Hosts

Alles, was sichtbar ist, und alles, was erlaubt ist, leitet sich aus genau einem Enum ab:

```mermaid
stateDiagram-v2
    [*] --> Idle: App-Start (immer manuell)
    Idle --> Pairing: Host erzeugt Ticket
    Pairing --> AwaitingConsent: Peer verbunden + SAS bestätigt
    AwaitingConsent --> Idle: abgelehnt / Timeout 60 s
    AwaitingConsent --> Sharing: Host klickt „Freigeben"

    Sharing --> Paused: Pause-Button / Overlay
    Paused --> Sharing: Fortsetzen (erneuter Klick)

    Sharing --> Terminating: Stop / Hotkey / Peer weg
    Paused --> Terminating: Stop / Hotkey
    AwaitingConsent --> Terminating: Hotkey
    Terminating --> Idle: alles abgebaut

    note right of Sharing
        Nur hier fließen Frames.
        Steuerung ist ein SEPARATES
        Flag — Sharing bedeutet nicht
        automatisch Control.
    end note

    note right of Paused
        Capture gestoppt,
        Verbindung bleibt,
        Viewer sieht Standbild
        mit „PAUSIERT"-Banner.
        Input ist HART blockiert.
    end note
```

`Sharing` und `controlGranted` sind absichtlich orthogonal. Der häufigste Fall ist "ich zeige dir
was, fass nichts an" — der muss der einfachste sein, und er ist der Standard.

---

## 1.8 Verzeichnisstruktur

```
AppControl/
├── docs/                       # Diese Dokumentation (7 Kapitel)
├── protocol/                   # Sprachneutrale Protokollspezifikation
│   └── schemas/                # JSON-Schemas der Control-Nachrichten
├── signaling/                  # Node/TypeScript Rendezvous-Server  ← läuft & getestet
├── tools/crypto-vectors/       # Referenz-Testvektoren für den Handshake
├── host-windows/               # .NET 8 / WPF Host-Anwendung
│   └── src/AppControl.Host/
│       ├── Core/               # SessionStateMachine — die zentrale Wahrheit
│       ├── Capture/            # Windows.Graphics.Capture + Fensterauswahl
│       ├── Encoding/           # Media Foundation H.264
│       ├── Input/              # SendInput, InputGuard, Emergency-Hotkey
│       ├── Net/                # Signaling-Client, WebRTC-Peer
│       ├── Security/           # Identität, Handshake, Trust-Store, SAS
│       ├── Ui/                 # Dashboard, App-Picker, Consent, Overlay, Tray
│       ├── Audit/              # Append-only Protokoll
│       └── Config/             # Einstellungen
└── viewer-macos/               # Swift 5.9 / SwiftUI Viewer-Anwendung
    └── Sources/AppControlViewer/
        ├── App/, UI/           # SwiftUI-Oberfläche
        ├── Net/                # Signaling, WebRTC
        ├── Security/           # Spiegelbild der Host-Krypto
        ├── Video/              # Metal-Rendering
        ├── Input/              # NSEvent/CGEventTap → Binärprotokoll
        └── Protocol/           # Nachrichtentypen
```

---

## 1.9 Was bewusst *nicht* gebaut ist

| Nicht enthalten | Grund |
|---|---|
| Unattended Access / Autostart | Widerspricht Leitplanke 1 und 2. Ein Tool, das ohne Anwesenden startet, ist ein RAT. |
| Mehrere gleichzeitige Viewer | Vergrößert die Angriffsfläche des Consent-Modells erheblich für einen Anwendungsfall, den es hier nicht gibt. |
| Dateiübertragung | Eigenständiger Vertrauens- und Malware-Vektor; gehört nicht in denselben Kanal wie Screen-Sharing. |
| Audio | Kein Teil der Anforderung. Der Medienpfad ist so gebaut, dass ein zweiter Track später ohne Umbau dazukommt. |
| Stealth-/Minimiert-Modus | Explizit ausgeschlossen. Siehe [`03-consent-and-transparency.md`, §3.9](03-consent-and-transparency.md). |
