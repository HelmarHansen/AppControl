#!/usr/bin/env python3
"""
Prüft, dass die JSON-Schemas die Nachrichten akzeptieren, die Host und Viewer
tatsächlich erzeugen — und die ablehnen, die sie nicht erzeugen dürfen.

Ein Schema, das nur danebenliegt und nie geprüft wird, ist schlimmer als keines:
Es sieht aus wie eine Zusicherung, ist aber keine.
"""
import json
import sys
from pathlib import Path

try:
    from jsonschema import Draft202012Validator
except ImportError:
    print("jsonschema fehlt:  pip install jsonschema")
    sys.exit(2)

ROOT = Path(__file__).resolve().parents[2]
SCHEMAS = ROOT / "protocol/schemas"

control = json.loads((SCHEMAS / "control-messages.schema.json").read_text())
signaling = json.loads((SCHEMAS / "signaling.schema.json").read_text())

failures = 0
checks = 0


def expect(label, schema, instance, should_pass):
    global failures, checks
    checks += 1
    errors = list(Draft202012Validator(schema).iter_errors(instance))
    passed = not errors
    if passed == should_pass:
        print(f"  \033[32m✓\033[0m {label}")
    else:
        failures += 1
        print(f"  \033[31m✗\033[0m {label}")
        if errors:
            print(f"      {errors[0].message}")
        else:
            print("      wurde akzeptiert, hätte aber abgelehnt werden müssen")


def sub(schema, ref):
    """Ein $defs-Teilschema mit den $defs des Wurzelschemas als Kontext."""
    return {**schema["$defs"][ref], "$defs": schema["$defs"]}


print("\n\033[1mControl-Nachrichten — gültige Beispiele\033[0m\n")

expect("share-state (Sharing, Fenster, ohne Steuerung)", control, {
    "t": "share-state", "state": "sharing",
    "scope": {"kind": "window", "title": "Visual Studio", "width": 1920, "height": 1080},
    "controlGranted": False, "elapsedSec": 724, "remainingSec": 1076,
}, True)

expect("share-state (pausiert, ohne Restzeit)", control, {
    "t": "share-state", "state": "paused",
    "scope": {"kind": "screen", "title": "Gesamter Bildschirm (Monitor 1)",
              "width": 2560, "height": 1440},
    "controlGranted": False, "elapsedSec": 12, "remainingSec": None,
}, True)

expect("share-state (gestoppt, ohne Scope)", control, {
    "t": "share-state", "state": "stopped", "scope": None,
    "controlGranted": False, "elapsedSec": 0,
}, True)

expect("control-state (Entzug durch Idle-Timeout)", control,
       {"t": "control-state", "granted": False, "reason": "idle-timeout"}, True)

expect("input-rejected (Gate 3)", control, {
    "t": "input-rejected", "gate": 3,
    "reason": "target-window-not-foreground", "count": 47,
}, True)

expect("stats", control, {
    "t": "stats", "fps": 58.2, "bitrateKbps": 4200,
    "rttMs": 18.4, "encodeMs": 3.1, "packetsLost": 0,
}, True)

expect("viewport (Viewer → Host)", control,
       {"t": "viewport", "width": 1600, "height": 900, "scale": 2.0}, True)

expect("cursor (arrow)", control, {"t": "cursor", "shape": "arrow", "visible": True}, True)
expect("cursor (versteckt)", control, {"t": "cursor", "shape": "arrow", "visible": False}, True)
expect("cursor (ibeam)", control, {"t": "cursor", "shape": "ibeam", "visible": True}, True)

expect("request-control", control, {"t": "request-control"}, True)
expect("ping",  control, {"t": "ping",  "id": 42, "tsMicros": 1712345678901234}, True)
expect("pong",  control, {"t": "pong",  "id": 42, "tsMicros": 1712345678901234}, True)
expect("bye",   control, {"t": "bye", "reason": "EmergencyHotkey"}, True)

print("\n\033[1mControl-Nachrichten — was abgelehnt werden muss\033[0m\n")

# Eine unbekannte Zeigerform muss auffallen: Der Viewer faellt sonst still auf
# den Pfeil zurueck, und niemand merkt, dass der Host etwas anderes meinte.
expect("cursor mit unbekannter Form", control,
       {"t": "cursor", "shape": "crosshair", "visible": True}, False)
expect("cursor ohne visible", control, {"t": "cursor", "shape": "arrow"}, False)

expect("share-state mit unbekanntem Zustand", control, {
    "t": "share-state", "state": "recording",
    "controlGranted": False, "elapsedSec": 0,
}, False)

expect("share-state ohne controlGranted", control,
       {"t": "share-state", "state": "sharing", "elapsedSec": 0}, False)

expect("control-state mit erfundenem Grund", control,
       {"t": "control-state", "granted": True, "reason": "weil-ich-es-kann"}, False)

expect("input-rejected mit Gate-Nummer außerhalb 0–5", control,
       {"t": "input-rejected", "gate": 9, "reason": "rate-limited", "count": 1}, False)

expect("scope mit negativer Breite", control, {
    "t": "share-state", "state": "sharing",
    "scope": {"kind": "window", "title": "x", "width": -1, "height": 100},
    "controlGranted": False, "elapsedSec": 0,
}, False)

expect("unbekannter Nachrichtentyp", control, {"t": "exfiltrate", "data": "..."}, False)

print("\n\033[1mSignaling — Client → Server\033[0m\n")

client = sub(signaling, "clientMessage")

expect("join (Host)", client,
       {"t": "join", "roomId": "ABCDEFGHIJKLMNOP", "role": "host", "v": "ac/1"}, True)
expect("join (Viewer, ohne Versionsangabe)", client,
       {"t": "join", "roomId": "ABCDEFGHIJKLMNOP", "role": "viewer"}, True)
expect("relay", client, {"t": "relay", "payload": "QUMxAAECAAAAAAAAAAAA"}, True)
expect("leave", client, {"t": "leave"}, True)

expect("join mit zu kurzer roomId", client,
       {"t": "join", "roomId": "kurz", "role": "host"}, False)
expect("join mit Pfad-Traversal in der roomId", client,
       {"t": "join", "roomId": "../../etc/passwd0", "role": "host"}, False)
expect("join mit erfundener Rolle", client,
       {"t": "join", "roomId": "ABCDEFGHIJKLMNOP", "role": "admin"}, False)
expect("relay mit übergroßem Payload", client,
       {"t": "relay", "payload": "x" * 200_000}, False)

print("\n\033[1mSignaling — Server → Client\033[0m\n")

server = sub(signaling, "serverMessage")

expect("joined", server,
       {"t": "joined", "roomId": "ABCDEFGHIJKLMNOP", "peerPresent": False}, True)
expect("peer-joined", server, {"t": "peer-joined", "role": "viewer"}, True)
expect("ice-servers (STUN + TURN mit kurzlebigen Zugangsdaten)", server, {
    "t": "ice-servers",
    "servers": [
        {"urls": ["stun:stun.l.google.com:19302"]},
        {"urls": ["turn:turn.example.com:3478"],
         "username": "1712349999:host", "credential": "abc123=="},
    ],
}, True)
expect("error", server, {
    "t": "error", "code": "role-taken",
    "message": "Rolle 'viewer' ist bereits belegt", "fatal": True,
}, True)

expect("error mit unbekanntem Code", server,
       {"t": "error", "code": "teapot", "message": "", "fatal": False}, False)

print()
if failures:
    print(f"\033[31m{failures} von {checks} Prüfungen fehlgeschlagen.\033[0m")
    sys.exit(1)
print(f"\033[32mAlle {checks} Schema-Prüfungen bestanden.\033[0m")
