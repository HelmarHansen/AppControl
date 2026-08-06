# 3 — Consent & Transparenz im Detail

Dieses Kapitel ist die eigentliche Spezifikation von AppControl. Alles andere ist Transport.

Die Anforderung war: Der Host muss jederzeit sehen, *dass* geteilt wird, *was* geteilt wird und
*ob* gesteuert wird; er muss mit einem Klick stoppen können; jede Sitzung braucht explizite
Zustimmung. Das ist unten in konkrete, prüfbare Mechanismen übersetzt.

---

## 3.1 Das Zustandsmodell — eine einzige Wahrheitsquelle

Der gesamte sichtbare und erlaubte Zustand des Hosts liegt in **einem** Objekt,
`Core/SessionStateMachine.cs`:

```csharp
public enum SessionState { Idle, Pairing, AwaitingConsent, Sharing, Paused, Terminating }

public sealed record SessionSnapshot(
    SessionState State,
    ShareScope?  Scope,           // FullScreen(monitor) | SingleWindow(hwnd, title, process)
    bool         ControlGranted,  // absichtlich getrennt vom State
    PeerIdentity Peer,            // Anzeigename + Fingerprint des Viewers
    DateTimeOffset? StartedAt,
    long         InjectedEventCount);
```

Zwei Regeln machen das belastbar:

1. **Jede Änderung geht durch die Zustandsmaschine.** Es gibt keine Methode, mit der ein anderer
   Teil des Programms `_isSharing = true` setzen könnte. Capture-Engine und Input-Injector besitzen
   selbst kein Zustandsflag; sie fragen bei jedem Frame und jedem Event.
2. **Jede Änderung erzeugt ein Event.** UI, Overlay, Tray-Icon und Audit-Log hängen alle an
   `SnapshotChanged`. Sie können nicht auseinanderlaufen, weil sie keine eigene Kopie halten.

Der praktische Nutzen: Es ist unmöglich, einen Zustand zu bauen, in dem gestreamt wird, das Overlay
aber etwas anderes anzeigt. Das ist genau der Fehler, der ein Transparenzversprechen wertlos macht.

---

## 3.2 Zustimmung beim Sitzungsstart

Wenn der Viewer verbunden ist und die SAS-Verifikation bestanden hat, sendet er
`consent-request`. Der Host zeigt daraufhin einen **modalen, fokussierten, nicht wegklickbaren**
Dialog:

```
┌────────────────────────────────────────────────────────────────┐
│  🔴  Zugriffsanfrage                                           │
├────────────────────────────────────────────────────────────────┤
│                                                                │
│   Jakob (MacBook Pro)  möchte deinen Bildschirm sehen          │
│                                                                │
│   Sicherheitscode:   🐙  🍒  🚀  🔔  🎩                        │
│   ⚠️  Vergleicht diese Symbole per Telefon/Chat.               │
│      Nur wenn sie übereinstimmen, ist die Verbindung sicher.   │
│                                                                │
│   Schlüssel-Fingerprint:  4f2a 9c81 … e07b   [✓ bekannt]      │
│                                                                │
│  ── Was möchtest du freigeben? ───────────────────────────────│
│   ( ) Gesamter Bildschirm      [Monitor 1 ▾]                   │
│   (•) Nur eine Anwendung       [ Auswählen … ]                 │
│                                                                │
│  ── Darf Jakob steuern? ──────────────────────────────────────│
│   (•) Nein — nur zusehen                        ← VORAUSGEWÄHLT│
│   ( ) Ja — Maus und Tastatur erlauben                          │
│       ⚠️ Jakob kann dann alles tun, was du tun kannst.         │
│                                                                │
│   Automatisch beenden nach:  [ 30 Minuten ▾ ]                  │
│                                                                │
│        [ Ablehnen ]                    [ Freigeben ]           │
│         ↑ Standard-Fokus                ↑ erst aktiv, wenn     │
│                                           Auswahl getroffen    │
│                                                                │
│   Anfrage läuft ab in 0:47                                     │
└────────────────────────────────────────────────────────────────┘
```

Sieben Details, die je einen konkreten Angriff oder Unfall verhindern:

| Detail | Verhindert |
|---|---|
| **Default = Ablehnen**, Fokus liegt auf „Ablehnen" | Ein versehentliches Enter/Leertaste gibt nicht frei. |
| **Default = „Nur zusehen"** | Steuerung wird nie beiläufig mitvergeben. Sie muss aktiv gewählt werden. |
| **„Freigeben" ist deaktiviert**, bis Scope gewählt ist | Kein Klick ohne Entscheidung, *was* geteilt wird. |
| **60-Sekunden-Timeout → automatisches Ablehnen** | Ein Dialog, der unbemerkt im Hintergrund steht, gibt niemals von selbst frei. |
| **SAS-Emojis** | Man-in-the-Middle durch einen bösartigen Signaling-Server. |
| **Fingerprint mit `[✓ bekannt]` / `[⚠️ NEU]`** | Ein Angreifer, der die Ticket-ID kennt, aber nicht der bekannte Peer ist. |
| **Auto-Stopp-Dauer** | Vergessene, endlos laufende Sitzungen. |

**Explizit nicht vorhanden:** kein „Nicht mehr fragen", kein „Diesem Gerät immer vertrauen", kein
Merken der letzten Auswahl. Der Dialog erscheint bei **jeder** Sitzung, auch bei bereits gepairten
Peers. Vertrauen in einen Schlüssel bedeutet „ich weiß, wer anfragt" — nicht „er darf jederzeit".

---

## 3.3 Der App-Picker mit Live-Vorschau

Bei „Nur eine Anwendung" öffnet sich ein Fenster mit allen sichtbaren Top-Level-Fenstern:

```
┌───────────────────────────────────────────────────────────┐
│  Was soll Jakob sehen?                          [Suche…]  │
├───────────────────────────────────────────────────────────┤
│  ┌───────────┐  ┌───────────┐  ┌───────────┐              │
│  │ [Live]    │  │ [Live]    │  │ [Live]    │              │
│  │ Visual    │  │ Chrome    │  │ Explorer  │              │
│  │ Studio    │  │           │  │           │              │
│  └───────────┘  └───────────┘  └───────────┘              │
│   Projekt.sln    3 Tabs         Downloads                 │
│                                                           │
│  ── Ganze Bildschirme ────────────────────────────────────│
│  ┌───────────┐  ┌───────────┐                             │
│  │ [Live]    │  │ [Live]    │                             │
│  │ Monitor 1 │  │ Monitor 2 │                             │
│  └───────────┘  └───────────┘                             │
│                                                           │
│  ⚠️ Ein geteiltes Fenster zeigt alles, was darin           │
│     erscheint — auch Benachrichtigungen und Dialoge.       │
│                                                           │
│                       [ Abbrechen ]  [ Diese teilen ]     │
└───────────────────────────────────────────────────────────┘
```

Die Vorschauen sind **echte Live-Streams** (je eine kurzlebige WGC-Session mit ~2 fps, siehe
`Capture/WindowThumbnailProvider.cs`), keine Icons und keine Standbilder. Der Host soll sehen, was
tatsächlich übertragen würde, bevor er zustimmt — inklusive dessen, was gerade im Fenster steht.

**Alternative Implementierung:** `GraphicsCapturePicker` ist Windows' eigener Auswahldialog und
spart den ganzen Code. Er ist aber nicht anpassbar — kein Warnhinweis, keine gemeinsame
Darstellung von Fenstern und Monitoren, keine Suche. Da die Auswahl Teil des Consent-Erlebnisses
ist, ist der eigene Picker die richtige Wahl. `Capture/WindowEnumerator.cs` dokumentiert beide
Wege; der Wechsel ist eine Zeile.

---

## 3.4 Das permanente Overlay

Sobald `State == Sharing` oder `Paused`, erscheint ein Fenster, das **nicht schließbar** ist:

```
   ┌──────────────────────────────────────────────────────────────────────┐
   │ 🔴 LIVE │ 📺 Visual Studio │ 🖱️ STEUERUNG AKTIV │ 👤 Jakob │ ⏱ 12:04 │
   │                                          [ ⏸ Pause ]  [ ⏹ Beenden ]  │
   └──────────────────────────────────────────────────────────────────────┘
```

Eigenschaften (`Ui/StatusOverlayWindow.xaml.cs`):

| Eigenschaft | Umsetzung | Zweck |
|---|---|---|
| Immer im Vordergrund | `Topmost=true`, alle 2 s per `SetWindowPos(HWND_TOPMOST)` erneuert | Kein anderes Fenster kann es dauerhaft verdecken |
| Nicht in Alt-Tab | `WS_EX_TOOLWINDOW` | Kann nicht versehentlich weggetabbt werden |
| Mausdurchlässig außer Buttons | `WS_EX_TRANSPARENT`, nur Button-Bereiche via Hit-Test aktiv | Stört die Arbeit nicht, bleibt aber bedienbar |
| Auf allen virtuellen Desktops | `IVirtualDesktopManager` | Kein Entkommen durch Desktop-Wechsel |
| Schließen = Freigabe beenden | `OnClosing` → `StateMachine.Stop()` | Das Overlay kann nicht ohne die Freigabe verschwinden |
| Nicht verschiebbar außerhalb des Sichtbereichs | Position wird auf Arbeitsbereich geklemmt | Kein „nach unten wegziehen" |
| Watchdog | Timer prüft alle 2 s Existenz + Sichtbarkeit, erstellt neu falls weg | Selbst bei Absturz des Overlay-Fensters wird es wiederhergestellt; scheitert das dreimal, wird die Sitzung beendet |

Der Watchdog ist der Punkt, an dem aus einer Anzeige eine Garantie wird: **Wenn das Overlay nicht
sichtbar sein kann, endet die Freigabe.** Es gibt keinen Zustand „streamt, aber Overlay ist weg".

Die Farbe folgt dem Zustand — rot für Sharing, orange für Pause, zusätzlich pulsierend wenn
Steuerung aktiv ist. Farbe allein trägt keine Information (die Textlabels sind eindeutig), damit
das auch bei Farbfehlsichtigkeit funktioniert.

**Zusätzlich, unabhängig von uns:** Windows zeichnet um jedes per WGC erfasste Fenster einen gelben
Rahmen. AppControl fordert die Capability `graphicsCaptureWithoutBorder` **nicht** an und setzt
`IsBorderRequired` nie auf `false`. Damit gibt es einen Transparenzhinweis, den nicht einmal ein
kompromittierter AppControl-Prozess abschalten könnte. Dass wir auf eine verfügbare Möglichkeit
zur Unsichtbarkeit verzichten, ist eine bewusste Designentscheidung.

---

## 3.5 Tray-Icon

Immer sichtbar, sobald die App läuft:

| Zustand | Icon | Tooltip |
|---|---|---|
| `Idle` | ⚪ grau | „AppControl — nicht verbunden" |
| `Pairing` / `AwaitingConsent` | 🟡 gelb, pulsierend | „AppControl — wartet auf deine Bestätigung" |
| `Sharing`, ohne Steuerung | 🔴 rot | „TEILT: Visual Studio · nur Ansicht · 12:04" |
| `Sharing`, mit Steuerung | 🔴 rot mit weißem Ring | „TEILT: Visual Studio · **STEUERUNG AKTIV** · 12:04" |
| `Paused` | 🟠 orange | „PAUSIERT — Jakob sieht ein Standbild" |

Kontextmenü: Status öffnen · Pause/Fortsetzen · **Sofort beenden** · Freigabe wechseln ·
Sitzungsprotokoll · Beenden.

---

## 3.6 Sofortiges Beenden und Pausieren

Vier gleichwertige Wege, jeweils **ein** Klick bzw. ein Griff:

1. **Overlay-Button „Beenden"** — immer sichtbar
2. **Tray-Kontextmenü → Sofort beenden**
3. **Notfall-Hotkey `Strg+Alt+Umschalt+X`** — global registriert
4. **Dreimal schnell die rechte Strg-Taste** — für den Fall, dass der Viewer Modifier hält

Was beim Auslösen passiert (`Core/SessionStateMachine.EmergencyStop()`), in dieser Reihenfolge:

```
1.  ControlGranted = false            ← ZUERST. Kein Event mehr, ab sofort.
2.  Alle gehaltenen Tasten/Buttons per SendInput freigeben  (siehe unten)
3.  CaptureEngine.Stop()              ← keine neuen Frames
4.  Encoder-Queue verwerfen           ← keine alten Frames im Puffer
5.  DataChannels schließen
6.  PeerConnection.Close()
7.  AuditLog: Grund + Zeitstempel
8.  Overlay ausblenden, Tray → grau
9.  Bestätigung anzeigen: „Freigabe beendet."
```

Schritt 2 ist der, den man übersieht: Wenn der Viewer im Moment des Abbruchs eine Taste gedrückt
hält, hat Windows sie als „unten" registriert. Ohne explizites `KEYEVENTF_KEYUP` für alle gehaltenen
Tasten bliebe der Host mit einer klemmenden Umschalttaste oder gedrückten Maustaste zurück. Der
`InputInjector` führt deshalb Buch über jede Taste und jeden Button, die er gedrückt hat
(`_heldKeys`, `_heldButtons`), und gibt sie beim Stopp garantiert frei.

**Der Hotkey ist doppelt implementiert.** `RegisterHotKey` ist der normale Weg. Er kann aber
theoretisch versagen, wenn ein anderer Prozess dieselbe Kombination belegt. Deshalb läuft parallel
ein `WH_KEYBOARD_LL`-Hook, der zusätzlich das Dreifach-Tippen der rechten Strg-Taste erkennt und
dabei per `LLKHF_INJECTED`-Flag **synthetische Events ignoriert** — der Viewer kann den Kill-Switch
also nicht selbst auslösen oder durch Tastenfluten unterdrücken.

**Pause** stoppt den Capture, hält aber die Verbindung: Der Viewer sieht das eingefrorene letzte
Bild mit deutlichem „PAUSIERT"-Banner (nicht ein weiterlaufendes Bild — das wäre irreführend), und
Input ist hart blockiert. Fortsetzen erfordert einen erneuten Klick des Hosts.

---

## 3.7 Die fünf Input-Gates

Jedes eingehende Event läuft durch `Input/InputGuard.cs`. Alle Prüfungen sind Deny-by-default:

### Gate 1 — Sitzungszustand
```csharp
if (snapshot.State != SessionState.Sharing) return Reject("nicht im Sharing-Zustand");
```
Deckt `Paused`, `Terminating`, `Idle` ab. Ein Event, das während des Verbindungsabbaus noch
eintrifft, wird verworfen.

### Gate 2 — Steuerungsfreigabe
```csharp
if (!snapshot.ControlGranted) return Reject("Steuerung nicht freigegeben");
```
Getrennt vom Zustand, damit „Steuerung entziehen" möglich ist, ohne die Freigabe zu beenden.

### Gate 3 — Fenster-Fokus (nur bei App-Scope)
```csharp
if (scope is SingleWindow w && GetForegroundWindow() != w.Hwnd)
    return Reject("Zielfenster nicht im Vordergrund");
```
Das ist das wichtigste Gate. Ohne es ist App-Freigabe eine Illusion: Der Viewer sieht zwar nur ein
Fenster, könnte aber mit Koordinaten außerhalb davon in jede andere Anwendung klicken. Mit dem Gate
gilt: Wechselt der Host selbst die Anwendung — etwa um ein Passwort einzugeben oder eine Mail zu
lesen — läuft jeder injizierte Klick sofort ins Leere. Der Viewer bekommt eine Statusmeldung
(„Zielfenster nicht aktiv"), damit klar ist, warum nichts passiert.

### Gate 4 — Geometrische Begrenzung
```csharp
var rect = scope.GetTargetRect();          // Fenster-Client-Rect oder Monitor-Rect
point = ClampToRect(point, rect);          // klemmen, nicht ablehnen
```
Normalisierte Koordinaten (0.0–1.0) werden auf das Zielrechteck abgebildet und dort geklemmt.
Geklemmt statt abgelehnt, weil ein Nutzer, der die Maus über den Rand zieht, ein Verhalten erwartet
und keine ignorierten Events. Das Rechteck wird bei jedem Event neu abgefragt — verschiebt der Host
das Fenster, folgt die Begrenzung sofort.

### Gate 5 — Tasten-Blocklist
```csharp
VK_LWIN, VK_RWIN                  → blockiert (Startmenü verlässt den Scope)
Alt+Tab, Alt+Esc, Ctrl+Esc        → blockiert (Fensterwechsel verlässt den Scope)
Ctrl+Alt+Del, Win+L               → technisch unmöglich (Secure Desktop), zusätzlich blockiert
Notfall-Hotkey-Kombination        → blockiert (Kill-Switch bleibt dem Host vorbehalten)
```
Nur aktiv bei App-Scope; bei Vollbild-Freigabe ist Fensterwechsel legitim (konfigurierbar über
`Config/Settings.cs → BlockSystemKeysOnFullScreen`).

**Jedes abgelehnte Event wird gezählt**, nach Grund gruppiert, und im Overlay angezeigt, wenn die
Rate ungewöhnlich ist. Ein Viewer, der 500 blockierte Win-Tastendrücke sendet, tut etwas, das der
Host sehen sollte.

---

## 3.8 Zusätzliche Sicherungen

| Mechanismus | Verhalten |
|---|---|
| **Inaktivitäts-Timeout** | 5 min ohne Input vom Viewer → Steuerung wird automatisch entzogen (Freigabe läuft weiter). Erneutes Erteilen erfordert einen Klick. |
| **Maximale Sitzungsdauer** | Im Consent-Dialog gewählt (Standard 30 min). Bei Ablauf: Freigabe endet, Dialog fragt nach Verlängerung. |
| **Bildschirmsperre** | `SessionSwitch`/`WTS_SESSION_LOCK` → sofortiger Stopp. Der Sperrbildschirm darf nie im Stream landen. |
| **UAC-Prompt erkannt** | Erscheint ein erhöhtes Fenster im Vordergrund, wird die Übertragung automatisch pausiert und der Viewer sieht „Host bearbeitet eine Systemanfrage". Der Secure Desktop wäre ohnehin schwarz — aber die Pause macht explizit, was sonst nur seltsam aussähe. |
| **Zwischenablage** | Wird **nicht** übertragen. Weder Text noch Dateien. Ein separater Kanal mit eigenem Consent wäre ein späteres Feature, kein stiller Teil des Input-Streams. |
| **Audit-Log** | Append-only JSON-Lines in `%LOCALAPPDATA%\AppControl\audit\`. Enthält: Sitzungsstart/-ende mit Grund, Consent-Entscheidungen, Scope-Wechsel, Control-Toggles, Peer-Fingerprint, Zahl injizierter und abgelehnter Events. Enthält **keine** Tastencodes — es soll nachvollziehbar machen, was passiert ist, ohne selbst ein Keylogger zu sein. |

---

## 3.9 Kein Stealth-Modus — eine Designentscheidung

Der Unterschied zwischen einem Screen-Sharing-Tool und einer Remote-Access-Trojaner ist nicht die
Technik. Es ist dieselbe Technik. Der Unterschied ist, ob die Person am Gerät weiß, was passiert.

Deshalb enthält dieser Code die folgenden Dinge nicht, und das ist keine offene Aufgabe:

- **Kein Autostart.** Der Installer schreibt keinen `Run`-Registry-Key, legt keinen Dienst und
  keine geplante Aufgabe an. Wer AppControl nutzt, startet es.
- **Kein Headless-Betrieb.** Es gibt kein `--silent`, kein `--no-ui`, kein Kommandozeilen-Flag,
  das eine Sitzung ohne Klick startet. `Program.cs` prüft beim Start, ob eine interaktive
  Desktop-Session vorhanden ist, und beendet sich sonst.
- **Kein Unterdrücken des Overlays.** Es gibt keinen Schalter, keine Einstellung, keinen
  Registry-Wert. Der Watchdog (§3.4) macht das Fehlen des Overlays zum Abbruchgrund.
- **Kein `graphicsCaptureWithoutBorder`.** Die App verzichtet auf die Möglichkeit, den gelben
  Systemrahmen zu entfernen.
- **Kein „immer vertrauen".** Jede Sitzung braucht einen Klick, auch die hundertste mit demselben
  Peer.

Wer diesen Code forkt, kann all das einbauen — das ist die Natur von Software. Die Absicht dieser
Architektur ist, dass es **auffallen würde**: Diese Eigenschaften sind nicht über den Code
verstreut, sondern in `SessionStateMachine`, `StatusOverlayWindow` und `InputGuard` konzentriert.
Ein Diff, der Transparenz entfernt, ist ein kurzer, sehr gut sichtbarer Diff.

Für den beschriebenen Anwendungsfall — ein Freund hilft einem Freund — ist der Host außerdem
derjenige, der die Software installiert und startet. Er ist die Person, die geschützt wird, und
er hat die Kontrolle. Genau so soll es sein.
