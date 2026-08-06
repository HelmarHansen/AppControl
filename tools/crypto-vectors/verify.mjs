#!/usr/bin/env node
/**
 * Prueft vectors.json gegen eine frische Berechnung — und, wichtiger, prueft die
 * Eigenschaften, die die Vektoren garantieren sollen:
 *
 *   - Host und Viewer kommen unabhaengig voneinander auf denselben Schluessel
 *   - der Umschlag laesst sich entsiegeln
 *   - eine Manipulation am Header oder Ciphertext wird erkannt
 *   - ein falscher PSK fuehrt nicht zu einem verwertbaren Ergebnis
 *
 * Laeuft in CI und nach jeder Aenderung an generate.mjs.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import { buildVectors, sessionKey, handshakeKey, sasEmoji, seal, open,
         PHASE_SESSION, DIR_HOST_TO_VIEWER, EMOJI_TABLE } from "./generate.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const stored = JSON.parse(readFileSync(join(here, "vectors.json"), "utf8"));
const fresh = buildVectors();
const hex = (s) => Buffer.from(s, "hex");

let checks = 0;
const check = (name, fn) => {
  fn();
  checks++;
  console.log(`  ✓ ${name}`);
};

console.log("AppControl Krypto-Testvektoren\n");

check("vectors.json ist aktuell (deterministisch reproduzierbar)", () => {
  assert.deepEqual(stored, fresh,
    "vectors.json weicht ab — 'node generate.mjs > vectors.json' erneut ausfuehren");
});

check("Host und Viewer leiten denselben Sitzungsschluessel ab", () => {
  const x = stored.x25519, h = stored.handshake;
  const hostSide = sessionKey({
    role: "host", ephPriv: hex(x.hostEphPriv), staticPriv: hex(x.hostStaticPriv),
    peerEphPub: hex(x.viewerEphPub), peerStaticPub: hex(x.viewerStaticPub),
    nonceHost: hex(h.nonceHost), nonceViewer: hex(h.nonceViewer) });
  const viewerSide = sessionKey({
    role: "viewer", ephPriv: hex(x.viewerEphPriv), staticPriv: hex(x.viewerStaticPriv),
    peerEphPub: hex(x.hostEphPub), peerStaticPub: hex(x.hostStaticPub),
    nonceHost: hex(h.nonceHost), nonceViewer: hex(h.nonceViewer) });

  assert.equal(hostSide.key.toString("hex"), viewerSide.key.toString("hex"));
  assert.equal(hostSide.key.toString("hex"), h.sessionKey);
});

check("Handshake-Schluessel haengt am PSK", () => {
  const p = stored.pairing;
  assert.equal(handshakeKey(hex(p.psk), hex(p.roomId)).toString("hex"), p.handshakeKey);

  const wrong = Buffer.from(hex(p.psk));
  wrong[0] ^= 0x01;
  assert.notEqual(handshakeKey(wrong, hex(p.roomId)).toString("hex"), p.handshakeKey,
    "ein einziges gekipptes Bit im PSK muss einen voellig anderen Schluessel ergeben");
});

check("SAS ist reproduzierbar und aus 64 gueltigen Symbolen", () => {
  const s = sasEmoji(hex(stored.handshake.sessionKey));
  assert.equal(s.bytes.toString("hex"), stored.sas.bytes);
  assert.deepEqual(s.indices, stored.sas.indices);
  assert.deepEqual(s.emoji, stored.sas.emoji);
  assert.equal(EMOJI_TABLE.length, 64);
  assert.equal(new Set(EMOJI_TABLE).size, 64, "Emojis muessen eindeutig sein");
  assert.ok(s.indices.every((i) => i >= 0 && i < 64));
});

check("Umschlag laesst sich versiegeln und wieder oeffnen", () => {
  const key = hex(stored.handshake.sessionKey);
  const sealed = Buffer.from(stored.envelope.sealed, "base64");
  const opened = open(key, sealed);
  assert.equal(opened.toString("base64"), stored.envelope.plaintext);
  assert.equal(sealed.subarray(0, 20).toString("hex"), stored.envelope.header);
});

check("manipulierter Ciphertext wird abgewiesen", () => {
  const key = hex(stored.handshake.sessionKey);
  const tampered = Buffer.from(stored.envelope.sealed, "base64");
  tampered[25] ^= 0x01;
  assert.throws(() => open(key, tampered), /unable to authenticate|bad decrypt|Unsupported state/i);
});

check("manipulierter Header wird abgewiesen (Header ist Associated Data)", () => {
  const key = hex(stored.handshake.sessionKey);
  const tampered = Buffer.from(stored.envelope.sealed, "base64");
  tampered[5] = 0x02;   // Richtung faelschen: host→viewer wird zu viewer→host
  assert.throws(() => open(key, tampered),
    /unable to authenticate|bad decrypt|Unsupported state/i,
    "eine gefaelschte Richtung im Header muss die AEAD-Pruefung brechen");
});

check("falscher Schluessel oeffnet den Umschlag nicht", () => {
  const wrong = Buffer.from(hex(stored.handshake.sessionKey));
  wrong[31] ^= 0xff;
  assert.throws(() => open(wrong, Buffer.from(stored.envelope.sealed, "base64")));
});

check("Zaehler und Salt landen unveraendert im Nonce", () => {
  const key = hex(stored.handshake.sessionKey);
  const salt4 = hex(stored.envelope.salt4);
  const s = seal(key, PHASE_SESSION, DIR_HOST_TO_VIEWER, 1, salt4,
                 Buffer.from(stored.envelope.plaintext, "base64"));
  assert.equal(s.toString("base64"), stored.envelope.sealed);

  // Ein anderer Zaehler MUSS einen anderen Ciphertext ergeben — sonst waere der
  // Nonce wiederverwendet, was ChaCha20-Poly1305 katastrophal bricht.
  const s2 = seal(key, PHASE_SESSION, DIR_HOST_TO_VIEWER, 2, salt4,
                  Buffer.from(stored.envelope.plaintext, "base64"));
  assert.notEqual(s2.subarray(20).toString("hex"), s.subarray(20).toString("hex"));
});

check("verschiedene Richtungen erzeugen verschiedene Ciphertexte", () => {
  const key = hex(stored.handshake.sessionKey);
  const salt4 = hex(stored.envelope.salt4);
  const pt = Buffer.from("gleicher klartext");
  const a = seal(key, PHASE_SESSION, 0x01, 7, salt4, pt);
  const b = seal(key, PHASE_SESSION, 0x02, 7, salt4, pt);
  assert.notEqual(a.toString("hex"), b.toString("hex"));
});

console.log(`\n${checks} Pruefungen bestanden.`);
