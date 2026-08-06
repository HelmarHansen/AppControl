# Protokoll

Sprachneutrale Definition. Verbindlich ist [`../docs/04-protocol.md`](../docs/04-protocol.md);
dieser Ordner enthält die maschinenlesbaren Ergänzungen.

| Datei | Inhalt |
|---|---|
| [`emoji-table.md`](emoji-table.md) | Die 64 SAS-Symbole in Protokollreihenfolge |
| [`schemas/`](schemas/) | JSON-Schemas der Control-Nachrichten |

## Warum die Duplikate geprüft werden

Das Protokoll ist dreimal implementiert — C# (Host), Swift (Viewer),
JavaScript (Signaling + Krypto-Referenz). Konstanten und Tabellen sind dadurch
dupliziert, und Duplikate laufen auseinander.

Zwei Mechanismen fangen das ab:

- **[`tools/crypto-vectors/`](../tools/crypto-vectors/)** prüft die *Berechnungen*:
  Host und Viewer rechnen jeden Schritt gegen dieselben Vektoren nach.
- **[`tools/consistency/check.py`](../tools/consistency/check.py)** prüft die
  *Deklarationen*: Emoji-Tabellen, Nachrichtentyp-Bytes, Header-Layouts,
  HKDF-Zeichenketten, HID-Abbildungen.

Zusammen decken sie den Bereich ab, in dem eine Änderung an einer Datei die
andere Seite still zerbricht.
