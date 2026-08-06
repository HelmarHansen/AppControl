# Krypto-Testvektoren

Referenzimplementierung des AppControl-Handshakes in Node.js — und die Vektoren, gegen die Host
(C#/NSec) und Viewer (Swift/swift-crypto) ihre eigene Implementierung prüfen.

## Warum das existiert

Dieselbe Kryptografie wird in diesem Projekt **dreimal** implementiert: einmal in C#, einmal in
Swift, einmal hier. Zwei unabhängige Implementierungen einer Spezifikation weichen erfahrungsgemäß
genau in den Details voneinander ab, die niemand testet — Byte-Reihenfolge des Zählers, Reihenfolge
der Nonces im Salt, Zuordnung der drei DH-Operationen zu den Rollen.

Solche Abweichungen äußern sich als „Verbindung schlägt fehl, Fehlermeldung unbrauchbar". Mit
gemeinsamen Testvektoren äußern sie sich als fehlschlagender Unit-Test mit klarer Ursache.

## Nutzung

```bash
node generate.mjs > vectors.json   # Vektoren neu erzeugen (deterministisch)
node verify.mjs                    # Vektoren + Sicherheitseigenschaften prüfen
```

`generate.mjs` ist deterministisch: Alle „Zufallszahlen" stammen aus einem HMAC mit fester
Ausgangszeichenkette. Zweimal ausführen ergibt bitgleiche Dateien — `verify.mjs` prüft genau das,
damit `vectors.json` nicht unbemerkt veralten kann.

## Was `verify.mjs` prüft

Nicht nur „die Zahlen stimmen noch", sondern die Eigenschaften, auf die sich das Protokoll verlässt:

- Host und Viewer kommen aus **unterschiedlichen** privaten Schlüsseln auf **denselben** Sitzungsschlüssel
- ein einziges gekipptes Bit im PSK ergibt einen völlig anderen Handshake-Schlüssel
- der Umschlag lässt sich entsiegeln, ein manipulierter **Ciphertext** aber nicht
- ein manipulierter **Header** ebenfalls nicht — er ist Associated Data, das beweist die Bindung
- ein anderer Zähler ergibt einen anderen Ciphertext (kein Nonce-Reuse)
- host→viewer und viewer→host erzeugen unterschiedliche Ciphertexte bei gleichem Klartext

## Wo die Ports das benutzen

| Port | Datei |
|---|---|
| Host (C#) | `host-windows/tests/AppControl.Host.Tests/CryptoVectorTests.cs` |
| Viewer (Swift) | `viewer-macos/Tests/AppControlViewerTests/CryptoVectorTests.swift` |

Beide lesen `vectors.json` ein und rechnen jeden Wert nach.
