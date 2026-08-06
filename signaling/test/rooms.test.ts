import { test } from "node:test";
import assert from "node:assert/strict";

import { RoomRegistry, type Participant } from "../src/rooms.js";
import type { Role, ServerMessage } from "../src/protocol.js";

/** Test-Doppel: sammelt Nachrichten statt sie zu senden. */
function fake(id: string, role: Role) {
  const sent: ServerMessage[] = [];
  let closed = false;
  const p: Participant = {
    id, role,
    send: (m) => sent.push(m),
    close: () => { closed = true; },
  };
  return { p, sent, isClosed: () => closed };
}

test("erster Teilnehmer betritt einen leeren Raum ohne Gegenpart", () => {
  const reg = new RoomRegistry(1000);
  const host = fake("h1", "host");

  const r = reg.join("roomAAAAAAAAAAAA", host.p);

  assert.equal(r.ok, true);
  assert.equal(r.ok && r.peerPresent, false);
  assert.equal(reg.size, 1);
});

test("zweiter Teilnehmer sieht den ersten und findet ihn als Gegenpart", () => {
  const reg = new RoomRegistry(1000);
  const host = fake("h1", "host");
  const viewer = fake("v1", "viewer");

  reg.join("roomAAAAAAAAAAAA", host.p);
  const r = reg.join("roomAAAAAAAAAAAA", viewer.p);

  assert.equal(r.ok && r.peerPresent, true);
  assert.equal(reg.peerOf("roomAAAAAAAAAAAA", viewer.p)?.id, "h1");
  assert.equal(reg.peerOf("roomAAAAAAAAAAAA", host.p)?.id, "v1");
});

test("dieselbe Rolle kann nicht doppelt belegt werden", () => {
  const reg = new RoomRegistry(1000);
  reg.join("roomAAAAAAAAAAAA", fake("h1", "host").p);

  const r = reg.join("roomAAAAAAAAAAAA", fake("h2", "host").p);

  assert.equal(r.ok, false);
  assert.equal(!r.ok && r.code, "role-taken");
});

test("ein dritter Teilnehmer kommt in keinem Fall in den Raum", () => {
  const reg = new RoomRegistry(1000);
  reg.join("roomAAAAAAAAAAAA", fake("h1", "host").p);
  reg.join("roomAAAAAAAAAAAA", fake("v1", "viewer").p);

  assert.equal(reg.join("roomAAAAAAAAAAAA", fake("h2", "host").p).ok, false);
  assert.equal(reg.join("roomAAAAAAAAAAAA", fake("v2", "viewer").p).ok, false);
});

test("Verlassen benachrichtigt den verbleibenden Teilnehmer", () => {
  const reg = new RoomRegistry(1000);
  const host = fake("h1", "host");
  const viewer = fake("v1", "viewer");
  reg.join("roomAAAAAAAAAAAA", host.p);
  reg.join("roomAAAAAAAAAAAA", viewer.p);

  reg.leave("roomAAAAAAAAAAAA", viewer.p);

  assert.deepEqual(host.sent, [{ t: "peer-left", role: "viewer" }]);
  assert.equal(reg.peerOf("roomAAAAAAAAAAAA", host.p), null);
});

test("nach Verlassen ist der Slot fuer einen neuen Teilnehmer frei", () => {
  const reg = new RoomRegistry(1000);
  const v1 = fake("v1", "viewer");
  reg.join("roomAAAAAAAAAAAA", fake("h1", "host").p);
  reg.join("roomAAAAAAAAAAAA", v1.p);
  reg.leave("roomAAAAAAAAAAAA", v1.p);

  assert.equal(reg.join("roomAAAAAAAAAAAA", fake("v2", "viewer").p).ok, true);
});

test("ein verspaetetes leave des Vorgaengers wirft den Nachfolger nicht raus", () => {
  // Realer Fall: Viewer-Socket bricht ab, Viewer verbindet sofort neu, und erst
  // danach feuert das close-Event des alten Sockets.
  const reg = new RoomRegistry(1000);
  const host = fake("h1", "host");
  const oldViewer = fake("v1", "viewer");
  const newViewer = fake("v2", "viewer");

  reg.join("roomAAAAAAAAAAAA", host.p);
  reg.join("roomAAAAAAAAAAAA", oldViewer.p);
  reg.leave("roomAAAAAAAAAAAA", oldViewer.p);
  reg.join("roomAAAAAAAAAAAA", newViewer.p);

  reg.leave("roomAAAAAAAAAAAA", oldViewer.p); // verspaetet

  assert.equal(reg.peerOf("roomAAAAAAAAAAAA", host.p)?.id, "v2",
    "der neue Viewer muss weiterhin erreichbar sein");
});

test("leere Raeume werden nach Ablauf der TTL entfernt", () => {
  const reg = new RoomRegistry(1000);
  const host = fake("h1", "host");
  reg.join("roomAAAAAAAAAAAA", host.p);
  reg.leave("roomAAAAAAAAAAAA", host.p);

  assert.equal(reg.sweep(Date.now() + 500), 0, "vor Ablauf der TTL bleibt der Raum");
  assert.equal(reg.sweep(Date.now() + 1500), 1, "nach Ablauf wird er entfernt");
  assert.equal(reg.size, 0);
});

test("besetzte Raeume werden nie entfernt", () => {
  const reg = new RoomRegistry(1);
  reg.join("roomAAAAAAAAAAAA", fake("h1", "host").p);

  assert.equal(reg.sweep(Date.now() + 10_000), 0);
  assert.equal(reg.size, 1);
});
