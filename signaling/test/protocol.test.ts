import { test } from "node:test";
import assert from "node:assert/strict";

import { parseClientMessage } from "../src/protocol.js";

const MAX = 1024;

test("gueltiges join wird angenommen", () => {
  const r = parseClientMessage(
    JSON.stringify({ t: "join", roomId: "ABCDEFGHIJKLMNOP", role: "host" }), MAX);
  assert.equal(r.ok, true);
  assert.deepEqual(r.message, { t: "join", roomId: "ABCDEFGHIJKLMNOP", role: "host" });
});

test("relay wird unveraendert durchgereicht", () => {
  const payload = "AAAAbase64ciphertext==";
  const r = parseClientMessage(JSON.stringify({ t: "relay", payload }), MAX);
  assert.equal(r.ok, true);
  assert.deepEqual(r.message, { t: "relay", payload });
});

test("kaputtes JSON wird abgelehnt", () => {
  const r = parseClientMessage("{nope", MAX);
  assert.equal(r.ok, false);
  assert.equal(r.error?.code, "bad-request");
});

test("Arrays und Skalare sind keine gueltigen Nachrichten", () => {
  for (const raw of ["[]", '"hi"', "42", "null"]) {
    assert.equal(parseClientMessage(raw, MAX).ok, false, `sollte ablehnen: ${raw}`);
  }
});

test("unbekannter Nachrichtentyp wird abgelehnt", () => {
  const r = parseClientMessage(JSON.stringify({ t: "hack" }), MAX);
  assert.equal(r.ok, false);
  assert.equal(r.error?.code, "bad-request");
});

test("roomId muss dem erlaubten Alphabet und der Laenge entsprechen", () => {
  const bad = [
    "zu-kurz",                        // < 16 Zeichen
    "A".repeat(129),                  // > 128 Zeichen
    "hat leerzeichen im namen!!",     // unerlaubte Zeichen
    "../../etc/passwd0000",           // Pfad-Traversal-Versuch
    "<script>alert(1)</script>",      // Injection-Versuch
  ];
  for (const roomId of bad) {
    const r = parseClientMessage(JSON.stringify({ t: "join", roomId, role: "host" }), MAX);
    assert.equal(r.ok, false, `sollte ablehnen: ${roomId}`);
  }
});

test("ungueltige Rolle wird abgelehnt", () => {
  const r = parseClientMessage(
    JSON.stringify({ t: "join", roomId: "ABCDEFGHIJKLMNOP", role: "admin" }), MAX);
  assert.equal(r.ok, false);
});

test("uebergrosser Payload wird abgelehnt statt weitergeleitet", () => {
  const r = parseClientMessage(
    JSON.stringify({ t: "relay", payload: "x".repeat(MAX + 1) }), MAX);
  assert.equal(r.ok, false);
  assert.equal(r.error?.code, "payload-too-large");
});

test("fremde Major-Version wird abgelehnt, gleiche Major-Version akzeptiert", () => {
  const mk = (v: string) =>
    parseClientMessage(JSON.stringify({ t: "join", roomId: "ABCDEFGHIJKLMNOP", role: "host", v }), MAX);

  assert.equal(mk("ac/2").ok, false);
  assert.equal(mk("ac/2").error?.code, "unsupported-version");
  assert.equal(mk("ac/1").ok, true);
  assert.equal(mk("ac/1.4").ok, true, "Minor-Versionen bleiben kompatibel");
});
