#!/usr/bin/env node
/**
 * Referenzimplementierung des AppControl-Handshakes + Erzeugung der Testvektoren.
 *
 * ZWECK: Host (C#/NSec) und Viewer (Swift/swift-crypto) implementieren dieselbe
 * Krypto zweimal. Zwei unabhaengige Implementierungen einer Spezifikation weichen
 * erfahrungsgemaess in genau den Details voneinander ab, die niemand testet —
 * Byte-Reihenfolge, Salt-Zusammensetzung, Reihenfolge der DH-Ergebnisse.
 *
 * Diese Datei ist die dritte, unabhaengige Implementierung und zugleich der
 * Schiedsrichter: Beide Ports pruefen ihre Ergebnisse gegen vectors.json.
 * Weicht einer ab, schlaegt sein Unit-Test fehl statt der Verbindungsaufbau
 * beim Nutzer.
 *
 * Nutzung:  node generate.mjs > vectors.json
 *           node verify.mjs            (prueft die Datei gegen sich selbst)
 */

import { createHash, createHmac, hkdfSync, randomBytes,
         createPublicKey, createPrivateKey, diffieHellman,
         createCipheriv, createDecipheriv } from "node:crypto";

// ── X25519 ohne externe Abhaengigkeiten ──────────────────────────────────────
// Node exponiert X25519 nur ueber KeyObjects. Um mit ROHEN 32-Byte-Schluesseln
// zu arbeiten (was Host und Viewer tun), verpacken wir sie in minimale
// DER-Strukturen. Die Praefixe sind fuer X25519 konstant.

const PKCS8_X25519_PREFIX = Buffer.from("302e020100300506032b656e04220420", "hex");
const SPKI_X25519_PREFIX  = Buffer.from("302a300506032b656e032100", "hex");

const privKeyFromRaw = (raw) =>
  createPrivateKey({ key: Buffer.concat([PKCS8_X25519_PREFIX, raw]), format: "der", type: "pkcs8" });

const pubKeyFromRaw = (raw) =>
  createPublicKey({ key: Buffer.concat([SPKI_X25519_PREFIX, raw]), format: "der", type: "spki" });

/** Rohen 32-Byte-Public-Key aus einem privaten Schluessel ableiten. */
function rawPublicFromPrivate(rawPriv) {
  const der = createPublicKey(privKeyFromRaw(rawPriv)).export({ format: "der", type: "spki" });
  return Buffer.from(der.subarray(der.length - 32));
}

/** X25519(privat, oeffentlich) → 32 Byte gemeinsames Geheimnis. */
export function x25519(rawPriv, rawPub) {
  return diffieHellman({ privateKey: privKeyFromRaw(rawPriv), publicKey: pubKeyFromRaw(rawPub) });
}

// ── KDF ──────────────────────────────────────────────────────────────────────

export function hkdf(ikm, salt, info, length) {
  return Buffer.from(hkdfSync("sha256", ikm, salt, Buffer.from(info, "utf8"), length));
}

const sha256 = (...parts) => createHash("sha256").update(Buffer.concat(parts)).digest();

// ── Handshake (docs/05-security.md §5.3) ─────────────────────────────────────

/** Schluessel fuer Phase A: leitet sich allein aus dem Pairing-Ticket ab. */
export function handshakeKey(psk, roomId) {
  return hkdf(psk, roomId, "ac/1 handshake", 32);
}

/**
 * Sitzungsschluessel aus dem Triple-DH.
 *
 * `role` bestimmt, welcher private Schluessel in dh_2 bzw. dh_3 eingeht — beide
 * Seiten muessen dieselben drei Werte berechnen, aber jede kennt nur ihre eigenen
 * privaten Schluessel. Die Zuordnung ist:
 *
 *     dh_1 = X25519(e_eigen, e_fremd)     — symmetrisch, beide kommen aufs Gleiche
 *     dh_2 = X25519(e_Host,  s_Viewer)    — Host nutzt e_priv, Viewer nutzt s_priv
 *     dh_3 = X25519(s_Host,  e_Viewer)    — Host nutzt s_priv, Viewer nutzt e_priv
 */
export function sessionKey({ role, ephPriv, staticPriv, peerEphPub, peerStaticPub, nonceHost, nonceViewer }) {
  const dh1 = x25519(ephPriv, peerEphPub);

  let dh2, dh3;
  if (role === "host") {
    dh2 = x25519(ephPriv, peerStaticPub);     // e_H × s_V
    dh3 = x25519(staticPriv, peerEphPub);     // s_H × e_V
  } else {
    dh2 = x25519(staticPriv, peerEphPub);     // s_V × e_H  ≡ e_H × s_V
    dh3 = x25519(ephPriv, peerStaticPub);     // e_V × s_H  ≡ s_H × e_V
  }

  const ikm = Buffer.concat([dh1, dh2, dh3]);
  const salt = sha256(nonceHost, nonceViewer);   // Reihenfolge: IMMER Host zuerst
  return { dh1, dh2, dh3, ikm, salt, key: hkdf(ikm, salt, "ac/1 session", 32) };
}

// ── SAS ──────────────────────────────────────────────────────────────────────

export const EMOJI_TABLE = [
  // 64 Symbole = 6 Bit pro Emoji. Auswahlkriterien: visuell klar unterscheidbar,
  // auf macOS und Windows aehnlich dargestellt, in beiden Sprachen leicht benennbar
  // (die Nutzer lesen sie sich am Telefon vor). Keine Flaggen, keine Hautfarben-
  // Modifier, keine Symbole, die sich zwischen Plattformen stark unterscheiden.
  // Die Reihenfolge ist Teil des Protokolls und darf sich nie aendern.
  "\u{1F419}", "\u{1F352}", "\u{1F680}", "\u{1F514}", "\u{1F3A9}", "\u{1F335}", "\u{1F418}", "\u{1F355}",
  "\u{2693}",  "\u{1F3B8}", "\u{1F98A}", "\u{1F344}", "\u{1F6B2}", "\u{1F319}", "\u{1F427}", "\u{1F34B}",
  "\u{1F3F0}", "\u{1F3BA}", "\u{1F98B}", "\u{1F345}", "\u{1F682}", "\u{1F31F}", "\u{1F422}", "\u{1F347}",
  "\u{26FA}",  "\u{1F3BB}", "\u{1F981}", "\u{1F349}", "\u{1F6F5}", "\u{1F308}", "\u{1F433}", "\u{1F35E}",
  "\u{1F5FF}", "\u{1F941}", "\u{1F989}", "\u{1F351}", "\u{1F681}", "\u{2600}",  "\u{1F41D}", "\u{1F955}",
  "\u{1F3D4}", "\u{1F3B9}", "\u{1F99C}", "\u{1F369}", "\u{26F5}",  "\u{1F33B}", "\u{1F42C}", "\u{1F9C0}",
  "\u{1F3AA}", "\u{1F3B7}", "\u{1F9A9}", "\u{1F353}", "\u{1F69C}", "\u{2744}",  "\u{1F43F}", "\u{1F330}",
  "\u{1F5FC}", "\u{1FA95}", "\u{1F994}", "\u{1F34D}", "\u{1F6F8}", "\u{1F340}", "\u{1F9AD}", "\u{1F968}",
];

export function sasEmoji(sessionKeyBytes) {
  const raw = hkdf(sessionKeyBytes, Buffer.alloc(0), "ac/1 sas", 5);
  return { bytes: raw, indices: [...raw].map((b) => b % 64), emoji: [...raw].map((b) => EMOJI_TABLE[b % 64]) };
}

// ── Umschlag (docs/04-protocol.md §4.2) ──────────────────────────────────────

export const PHASE_HANDSHAKE = 0x01;
export const PHASE_SESSION   = 0x02;
export const DIR_HOST_TO_VIEWER = 0x01;
export const DIR_VIEWER_TO_HOST = 0x02;

/** 20-Byte-Header; er ist zugleich Associated Data der AEAD. */
export function envelopeHeader(phase, direction, counter, salt4) {
  const h = Buffer.alloc(20);
  h.write("AC1\0", 0, "latin1");
  h[4] = phase;
  h[5] = direction;
  h.writeUInt16LE(0, 6);
  h.writeBigUInt64BE(BigInt(counter), 8);   // Zaehler big-endian, damit er lexikografisch sortiert
  salt4.copy(h, 16);
  return h;
}

export function seal(key, phase, direction, counter, salt4, plaintext) {
  const header = envelopeHeader(phase, direction, counter, salt4);
  const nonce = header.subarray(8, 20);                    // 12 Byte: Zaehler(8) ‖ Salt(4)
  const cipher = createCipheriv("chacha20-poly1305", key, nonce, { authTagLength: 16 });
  cipher.setAAD(header);
  const ct = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  return Buffer.concat([header, ct, cipher.getAuthTag()]);
}

export function open(key, envelope) {
  const header = envelope.subarray(0, 20);
  if (header.subarray(0, 4).toString("latin1") !== "AC1\0") throw new Error("falsches Magic");
  const nonce = header.subarray(8, 20);
  const tag = envelope.subarray(envelope.length - 16);
  const ct = envelope.subarray(20, envelope.length - 16);
  const decipher = createDecipheriv("chacha20-poly1305", key, nonce, { authTagLength: 16 });
  decipher.setAAD(header);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(ct), decipher.final()]);
}

// ── Vektorerzeugung ──────────────────────────────────────────────────────────

/** Deterministische "Zufallszahlen", damit die Vektoren reproduzierbar sind. */
function det(label, n) {
  let out = Buffer.alloc(0), i = 0;
  while (out.length < n) {
    out = Buffer.concat([out, createHmac("sha256", "appcontrol-test-vectors").update(`${label}:${i++}`).digest()]);
  }
  return out.subarray(0, n);
}

function buildVectors() {
  const hex = (b) => Buffer.from(b).toString("hex");
  const b64 = (b) => Buffer.from(b).toString("base64");

  // Feste Schluesselpaare
  const hostStaticPriv   = det("host-static", 32);
  const hostEphPriv      = det("host-eph", 32);
  const viewerStaticPriv = det("viewer-static", 32);
  const viewerEphPriv    = det("viewer-eph", 32);

  const hostStaticPub   = rawPublicFromPrivate(hostStaticPriv);
  const hostEphPub      = rawPublicFromPrivate(hostEphPriv);
  const viewerStaticPub = rawPublicFromPrivate(viewerStaticPriv);
  const viewerEphPub    = rawPublicFromPrivate(viewerEphPriv);

  const psk         = det("psk", 32);
  const roomId      = det("room", 16);
  const nonceHost   = det("nonce-host", 16);
  const nonceViewer = det("nonce-viewer", 16);

  const kHs = handshakeKey(psk, roomId);

  const hostSide = sessionKey({
    role: "host", ephPriv: hostEphPriv, staticPriv: hostStaticPriv,
    peerEphPub: viewerEphPub, peerStaticPub: viewerStaticPub, nonceHost, nonceViewer });

  const viewerSide = sessionKey({
    role: "viewer", ephPriv: viewerEphPriv, staticPriv: viewerStaticPriv,
    peerEphPub: hostEphPub, peerStaticPub: hostStaticPub, nonceHost, nonceViewer });

  if (!hostSide.key.equals(viewerSide.key)) {
    throw new Error("FATAL: Host und Viewer kommen auf unterschiedliche Sitzungsschluessel");
  }

  const sas = sasEmoji(hostSide.key);

  const plaintext = Buffer.from(
    JSON.stringify({ t: "sdp", kind: "offer", sdp: "v=0\r\no=- 1 1 IN IP4 0.0.0.0\r\n" }), "utf8");
  const salt4 = det("env-salt", 4);
  const sealed = seal(hostSide.key, PHASE_SESSION, DIR_HOST_TO_VIEWER, 1, salt4, plaintext);

  return {
    _comment: [
      "Testvektoren fuer den AppControl-Handshake (ac/1).",
      "Erzeugt von tools/crypto-vectors/generate.mjs — NICHT von Hand bearbeiten.",
      "Host (C#) und Viewer (Swift) muessen jeden dieser Werte reproduzieren.",
    ],
    protocol: "ac/1",
    x25519: {
      hostStaticPriv:   hex(hostStaticPriv),   hostStaticPub:   hex(hostStaticPub),
      hostEphPriv:      hex(hostEphPriv),      hostEphPub:      hex(hostEphPub),
      viewerStaticPriv: hex(viewerStaticPriv), viewerStaticPub: hex(viewerStaticPub),
      viewerEphPriv:    hex(viewerEphPriv),    viewerEphPub:    hex(viewerEphPub),
    },
    pairing: { psk: hex(psk), roomId: hex(roomId), handshakeKey: hex(kHs) },
    handshake: {
      nonceHost: hex(nonceHost), nonceViewer: hex(nonceViewer),
      dh1: hex(hostSide.dh1), dh2: hex(hostSide.dh2), dh3: hex(hostSide.dh3),
      salt: hex(hostSide.salt),
      sessionKey: hex(hostSide.key),
    },
    sas: { bytes: hex(sas.bytes), indices: sas.indices, emoji: sas.emoji },
    envelope: {
      phase: PHASE_SESSION, direction: DIR_HOST_TO_VIEWER, counter: 1,
      salt4: hex(salt4),
      header: hex(sealed.subarray(0, 20)),
      plaintext: b64(plaintext),
      sealed: b64(sealed),
    },
    hkdfCases: [
      { ikm: hex(Buffer.alloc(32)), salt: "", info: "ac/1 session", length: 32,
        out: hex(hkdf(Buffer.alloc(32), Buffer.alloc(0), "ac/1 session", 32)) },
      { ikm: hex(Buffer.from("00010203040506070809", "hex")), salt: hex(Buffer.from("aabb", "hex")),
        info: "ac/1 sas", length: 5,
        out: hex(hkdf(Buffer.from("00010203040506070809", "hex"), Buffer.from("aabb", "hex"), "ac/1 sas", 5)) },
    ],
  };
}

if (import.meta.url === `file://${process.argv[1]}`) {
  process.stdout.write(JSON.stringify(buildVectors(), null, 2) + "\n");
}

export { buildVectors, rawPublicFromPrivate, det };
