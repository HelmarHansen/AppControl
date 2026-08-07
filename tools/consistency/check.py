#!/usr/bin/env python3
"""
Prüft die drei Implementierungen des AppControl-Protokolls gegeneinander.

WARUM DAS EXISTIERT: Das Protokoll ist dreimal implementiert — C# (Host),
Swift (Viewer), JavaScript (Referenz/Signaling). Konstanten, Byte-Layouts und
Tabellen sind dadurch dupliziert, und Duplikate laufen auseinander.

Die Krypto-Testvektoren fangen Abweichungen in den Berechnungen ab. Dieses
Skript fängt Abweichungen in den *Deklarationen* ab: Emoji-Tabellen,
Nachrichtentyp-Bytes, Header-Größen, HID-Abbildungen. Beides zusammen deckt
den Bereich ab, in dem eine Änderung an einer Datei die andere Seite still
zerbricht.

Läuft ohne Swift- oder .NET-Toolchain — es ist eine Quelltextanalyse, kein
Compiler-Lauf. Das ist eine bewusste Einschränkung: Es prüft, was deklariert
ist, nicht was der Compiler daraus macht.

Nutzung:  python3 tools/consistency/check.py
Exit 0 = alles konsistent, Exit 1 = mindestens eine Abweichung.
"""

import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HOST = ROOT / "host-windows/src/AppControl.Host"
VIEWER = ROOT / "viewer-macos/Sources/AppControlViewer"
TOOLS = ROOT / "tools/crypto-vectors"

failures: list[str] = []
checks = 0


def check(name: str, condition: bool, detail: str = "") -> None:
    global checks
    checks += 1
    if condition:
        print(f"  \033[32m✓\033[0m {name}")
    else:
        print(f"  \033[31m✗\033[0m {name}")
        if detail:
            for line in detail.strip().splitlines():
                print(f"      {line}")
        failures.append(name)


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def read_code(path: Path) -> str:
    """
    Quelltext ohne Kommentare.

    Nötig für Prüfungen auf Abwesenheit: CaptureEngine.cs *erklärt* im
    Kommentar, warum `IsBorderRequired = false` bewusst nicht gesetzt wird.
    Eine naive Textsuche würde diese Erklärung als Verstoß werten — und die
    Prüfung damit unbrauchbar machen, weil man den Kommentar löschen müsste,
    um sie zu bestehen.
    """
    text = read(path)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)    # Blockkommentare
    text = re.sub(r"^\s*//.*$", "", text, flags=re.MULTILINE)  # Zeilenkommentare
    text = re.sub(r"<!--.*?-->", "", text, flags=re.DOTALL)     # XML/HTML-Kommentare
    return text


# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mEmoji-Tabelle (SAS)\033[0m")
print("  Die Reihenfolge ist Teil des Protokolls. Weicht sie ab, zeigen Host und")
print("  Viewer verschiedene Symbole für denselben Schlüssel — und die Nutzer")
print("  brechen eine völlig gesunde Verbindung ab.\n")

cs_emoji = [
    chr(int(m, 16))
    for m in re.findall(r'"\\U([0-9A-Fa-f]{8})"', read(HOST / "Security/EmojiTable.cs"))
]
swift_emoji = [
    "".join(chr(int(c, 16)) for c in re.findall(r"\\u\{([0-9A-Fa-f]+)\}", entry))
    for entry in re.findall(r'"((?:\\u\{[0-9A-Fa-f]+\})+)"', read(VIEWER / "Security/EmojiTable.swift"))
]
js_emoji = json.loads(
    subprocess.run(
        ["node", "--input-type=module", "-e",
         f"import {{EMOJI_TABLE}} from '{TOOLS}/generate.mjs'; console.log(JSON.stringify(EMOJI_TABLE));"],
        capture_output=True, text=True, check=True).stdout
)

check("C#-Tabelle hat 64 Einträge", len(cs_emoji) == 64, f"gefunden: {len(cs_emoji)}")
check("Swift-Tabelle hat 64 Einträge", len(swift_emoji) == 64, f"gefunden: {len(swift_emoji)}")
check("JS-Tabelle hat 64 Einträge", len(js_emoji) == 64, f"gefunden: {len(js_emoji)}")

diff_cs_swift = [
    f"Index {i}: C#={c!r} Swift={s!r}"
    for i, (c, s) in enumerate(zip(cs_emoji, swift_emoji)) if c != s
]
check("C# und Swift stimmen exakt überein", not diff_cs_swift, "\n".join(diff_cs_swift[:10]))

diff_cs_js = [
    f"Index {i}: C#={c!r} JS={j!r}"
    for i, (c, j) in enumerate(zip(cs_emoji, js_emoji)) if c != j
]
check("C# und JS stimmen exakt überein", not diff_cs_js, "\n".join(diff_cs_js[:10]))

check("alle Symbole sind eindeutig", len(set(cs_emoji)) == 64,
      f"eindeutige: {len(set(cs_emoji))}")

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mNachrichtentypen (Binärprotokoll)\033[0m")
print("  Ein Versatz hier äußert sich als 'Maus springt wild herum' — schwer zu")
print("  diagnostizieren, wenn man nicht auf die Rohbytes schaut.\n")

cs_types = dict(re.findall(
    r"(\w+)\s*=\s*0x([0-9A-Fa-f]{2}),", read(HOST / "Input/InputMessages.cs")))
swift_types = dict(re.findall(
    r"case (\w+)\s*=\s*0x([0-9A-Fa-f]{2})", read(VIEWER / "Input/InputEncoder.swift")))

expected = {
    "mouseMove": "01", "mouseButton": "02", "mouseScroll": "03",
    "key": "04", "text": "05", "releaseAll": "06",
}


def normalize(mapping: dict) -> dict:
    """Vergleicht case-insensitiv nach Name und Wert (C# nutzt PascalCase, Swift camelCase)."""
    return {k.lower(): v.lower() for k, v in mapping.items()}


cs_norm, swift_norm, exp_norm = normalize(cs_types), normalize(swift_types), normalize(expected)

for name, value in exp_norm.items():
    check(f"{name} = 0x{value.upper()} in beiden Implementierungen",
          cs_norm.get(name) == value and swift_norm.get(name) == value,
          f"C#={cs_norm.get(name)} Swift={swift_norm.get(name)} erwartet={value}")

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mUmschlag-Format (SecureChannel)\033[0m")

cs_channel = read(HOST / "Security/SecureChannel.cs")
swift_channel = read(VIEWER / "Security/SecureChannel.swift")

check("Header ist beidseitig 20 Byte",
      "HeaderSize = 20" in cs_channel and "headerSize = 20" in swift_channel)
check("Magic ist beidseitig \"AC1\\0\"",
      'AC1\\0"u8' in cs_channel and 'Array("AC1\\0".utf8)' in swift_channel)
check("Zähler ist beidseitig Big-Endian",
      "WriteUInt64BigEndian" in cs_channel and "counter.bigEndian" in swift_channel,
      "Die Byte-Reihenfolge des Zählers geht in den Nonce ein — eine Abweichung\n"
      "bricht jede Entschlüsselung, aber erst zur Laufzeit.")
check("Phase-Werte stimmen überein",
      "Handshake = 0x01" in cs_channel and "handshake = 0x01" in swift_channel)
check("Richtungs-Werte stimmen überein",
      "HostToViewer = 0x01" in cs_channel and "hostToViewer = 0x01" in swift_channel)

for name, marker_cs, marker_swift in [
    ("Replay-Schutz", "highestReceivedCounter", "highestReceivedCounter"),
    ("Richtungsprüfung", "ReceiveDirection", "receiveDirection"),
]:
    check(f"{name} in beiden Implementierungen vorhanden",
          marker_cs in cs_channel and marker_swift in swift_channel)

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mHKDF-Info-Zeichenketten\033[0m")
print("  Ein Tippfehler ergibt einen anderen Schlüssel — und eine Fehlermeldung,")
print("  die nur 'Entschlüsselung fehlgeschlagen' sagt.\n")

cs_handshake = read(HOST / "Security/Handshake.cs")
swift_handshake = read(VIEWER / "Security/Handshake.swift")
js_source = read(TOOLS / "generate.mjs")

for label in ["ac/1 handshake", "ac/1 session", "ac/1 sas"]:
    present = [
        ("C#", f'"{label}"' in cs_handshake),
        ("Swift", f'"{label}"' in swift_handshake),
        ("JS", f'"{label}"' in js_source),
    ]
    missing = [name for name, ok in present if not ok]
    check(f"'{label}' in allen drei Implementierungen",
          not missing, f"fehlt in: {', '.join(missing)}")

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mHID-Tastenabbildung (Viewer → Host)\033[0m")
print("  Der Viewer erzeugt HID-Usages, der Host bildet sie auf Scancodes ab.")
print("  Eine Usage, die der Viewer sendet, der Host aber nicht kennt, wird")
print("  stillschweigend verworfen — die Taste tut dann einfach nichts.\n")

swift_usages = {
    int(usage, 16)
    for _, usage in re.findall(r"0x([0-9A-Fa-f]{2}):\s*0x([0-9A-Fa-f]{2}),",
                               read(VIEWER / "Input/HidKeyMap.swift"))
}
cs_usages = {
    int(usage, 16)
    for usage in re.findall(r"\[0x([0-9A-Fa-f]{2})\]\s*=", read(HOST / "Input/HidKeyMap.cs"))
}

check(f"Viewer bildet {len(swift_usages)} Tasten ab", len(swift_usages) > 100,
      f"gefunden: {len(swift_usages)}")
check(f"Host kennt {len(cs_usages)} HID-Usages", len(cs_usages) > 100,
      f"gefunden: {len(cs_usages)}")

orphans = sorted(swift_usages - cs_usages)
check("jede vom Viewer gesendete Usage ist dem Host bekannt",
      not orphans,
      "Der Host kennt diese Usages nicht: " + ", ".join(f"0x{u:02X}" for u in orphans))

# Blocklist-Konstanten müssen zu den tatsächlichen Usages passen
cs_guard = read(HOST / "Input/InputGuard.cs")
for const, expected_usage in [("LeftGui", 0xE3), ("RightGui", 0xE7),
                              ("Tab", 0x2B), ("Escape", 0x29), ("X", 0x1B)]:
    declared = re.search(rf"{const}\s*=\s*0x([0-9A-Fa-f]+);", read(HOST / "Input/HidKeyMap.cs"))
    check(f"Blocklist-Konstante {const} = 0x{expected_usage:02X}",
          declared is not None and int(declared.group(1), 16) == expected_usage,
          f"deklariert: {declared.group(1) if declared else 'fehlt'}")

check("linke Command-Taste (macOS) wird zur Windows-Taste und ist geblockt",
      0xE3 in swift_usages and "LeftGui" in cs_guard,
      "Cmd → GUI-Usage → auf dem Host die Windows-Taste. Sie muss bei\n"
      "App-Freigabe blockiert sein, sonst öffnet der Viewer das Startmenü.")

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mProtokollversion\033[0m")

versions = {
    "C# Handshake": 'Version { get; init; } = "ac/1"' in cs_handshake,
    "Swift Handshake": 'var v: String = "ac/1"' in swift_handshake,
    "Signaling-Server": 'PROTOCOL_VERSION = "ac/1"' in read(ROOT / "signaling/src/protocol.ts"),
    "C# SignalingClient": 'v = "ac/1"' in read(HOST / "Net/SignalingClient.cs"),
    "Swift SignalingClient": '"v": "ac/1"' in read(VIEWER / "Net/SignalingClient.swift"),
}
missing = [name for name, ok in versions.items() if not ok]
check("alle Komponenten melden Version 'ac/1'", not missing,
      f"nicht gefunden in: {', '.join(missing)}")

# ─────────────────────────────────────────────────────────────────────────────
print("\n\033[1mTransparenz-Zusicherungen\033[0m")
print("  Diese Prüfungen sind ungewöhnlich für einen Konsistenz-Check, aber sie")
print("  schützen genau das, was AppControl ausmacht: Ein Commit, der eine")
print("  dieser Eigenschaften entfernt, soll rot werden, nicht durchrutschen.\n")

capture = read_code(HOST / "Capture/CaptureEngine.cs")
check("IsBorderRequired wird NICHT auf false gesetzt",
      not re.search(r"IsBorderRequired\s*=\s*false", capture),
      "Der gelbe Systemrahmen ist ein vom Betriebssystem garantierter\n"
      "Transparenzhinweis. Ihn abzuschalten wäre die eine Änderung, die\n"
      "AppControl in ein Überwachungswerkzeug verwandelt.")

check("graphicsCaptureWithoutBorder wird nicht angefordert",
      "graphicsCaptureWithoutBorder" not in read_code(HOST / "app.manifest"))

check("Anwendung läuft nicht als Administrator",
      'level="asInvoker"' in read_code(HOST / "app.manifest"),
      "Mit Adminrechten könnte AppControl in erhöhte Fenster injizieren —\n"
      "also die Grenze überschreiten, die Windows zwischen normalen und\n"
      "privilegierten Anwendungen zieht.")

app_cs = read(HOST / "App.xaml.cs")
check("unsichtbare Startmodi werden abgelehnt",
      all(flag in app_cs for flag in ["--silent", "--no-ui", "--headless", "--autostart"]))
check("nicht-interaktive Sitzungen werden abgelehnt",
      "Environment.UserInteractive" in app_cs)

overlay = read(HOST / "Ui/StatusOverlayWindow.xaml.cs")
check("Overlay-Watchdog beendet die Sitzung, wenn er versagt",
      "StopReason.OverlayUnavailable" in overlay,
      "Ohne diesen Pfad wäre das Overlay eine Anzeige. Mit ihm ist es eine\n"
      "Vorbedingung: Kann es nicht dargestellt werden, endet die Freigabe.")
check("Overlay kann während der Freigabe nicht geschlossen werden",
      "_allowClose" in overlay and "e.Cancel = true" in overlay)

consent = read(HOST / "Ui/ConsentDialog.xaml")
check("Consent-Dialog hat 'Ablehnen' als Standardaktion",
      'IsCancel="True" IsDefault="True"' in consent,
      "Enter und Escape müssen beide ablehnen — ein versehentlicher\n"
      "Tastendruck darf nichts freigeben.")
check("'Freigeben' ist zunächst deaktiviert",
      'x:Name="GrantButton"' in consent and 'IsEnabled="False"' in consent)
check("'Nur zusehen' ist vorausgewählt",
      'x:Name="ControlNoRadio"' in consent and 'IsChecked="True"' in consent,
      "Steuerung darf nie beiläufig mitvergeben werden.")

state_machine = read(HOST / "Core/SessionStateMachine.cs")
check("Pausieren entzieht die Steuerung hart",
      "State = SessionState.Paused, ControlGranted = false" in state_machine)
check("Fortsetzen stellt die Steuerung nicht wieder her",
      "State = SessionState.Sharing }" in state_machine)

hotkey = read(HOST / "Input/EmergencyHotkey.cs")
check("Notfall-Hotkey ignoriert synthetische Tastendrücke",
      "LLKHF_INJECTED" in hotkey,
      "Ohne diese Prüfung könnte der Viewer den Kill-Switch selbst auslösen\n"
      "oder durch Tastenfluten die Erkennung des echten Drucks stören.")

injector = read(HOST / "Input/InputInjector.cs")
check("Injector führt Buch über gehaltene Tasten",
      "_heldKeys" in injector and "_heldButtons" in injector,
      "Ohne dieses Buch bliebe der Host beim Notfall-Stopp mit einer\n"
      "klemmenden Taste zurück, wenn der Viewer gerade etwas hielt.")

guard = read(HOST / "Input/InputGuard.cs")
for gate, marker in [
    ("Gate 1 (Sitzungszustand)", "GateRejection.NotSharing"),
    ("Gate 2 (Steuerungsfreigabe)", "GateRejection.ControlNotGranted"),
    ("Gate 3 (Fenster im Vordergrund)", "GateRejection.WindowNotForeground"),
    ("Gate 4 (Geometrie)", "GateRejection.TargetRectInvalid"),
    ("Gate 5 (Tasten-Blocklist)", "GateRejection.KeyBlocked"),
]:
    check(f"{gate} ist implementiert", marker in guard)

check("NaN wird vor der Koordinatenberechnung abgefangen",
      "IsFinite" in guard,
      "Math.Clamp(NaN, 0, 1) gibt NaN zurück — beide Vergleiche sind bei NaN\n"
      "false. Ohne die Prüfung liefe NaN bis in SendInput.")

# ── Testvektoren: eine Quelle, zwei Kopien ───────────────────────────────────
#
# Der Windows-Test bindet tools/crypto-vectors/vectors.json ueber einen
# MSBuild-Link ein - das geht ueber Projektgrenzen hinweg. SwiftPM kann das
# nicht: Ressourcen muessen im Target-Verzeichnis liegen, und ein Symlink dorthin
# wird als Symlink ins Bundle kopiert, wo er ins Leere zeigt. Genau daran sind
# die Viewer-Tests in CI-Lauf 9 gescheitert.
#
# Deshalb liegt unter viewer-macos/ eine echte Kopie. Zwei Kopien heisst: Sie
# koennen auseinanderlaufen. Diese Pruefung ist der Grund, warum sie es nicht
# unbemerkt tun.
vectors_source = ROOT / "tools/crypto-vectors/vectors.json"
vectors_viewer = ROOT / "viewer-macos/Tests/AppControlViewerTests/vectors.json"
check("Testvektoren des Viewers stimmen mit der Quelle ueberein",
      vectors_viewer.exists() and
      vectors_viewer.read_bytes() == vectors_source.read_bytes(),
      "Abgleichen mit:\n"
      "  cp tools/crypto-vectors/vectors.json viewer-macos/Tests/AppControlViewerTests/")

check("Die Viewer-Kopie ist eine echte Datei, kein Symlink",
      vectors_viewer.exists() and not vectors_viewer.is_symlink(),
      "SwiftPM kopiert Symlinks als Symlinks ins Ressourcenbuendel -\n"
      "dort zeigen sie ins Leere und der Test bricht mit fatalError ab.")

# ─────────────────────────────────────────────────────────────────────────────
print()
if failures:
    print(f"\033[31m{len(failures)} von {checks} Prüfungen fehlgeschlagen:\033[0m")
    for name in failures:
        print(f"  · {name}")
    sys.exit(1)

print(f"\033[32mAlle {checks} Prüfungen bestanden.\033[0m")
