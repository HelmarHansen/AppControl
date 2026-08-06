/** Konfiguration, komplett ueber Umgebungsvariablen. Keine Config-Datei, kein State. */

function envInt(name: string, fallback: number, min = 1): number {
  const raw = process.env[name];
  if (raw === undefined || raw === "") return fallback;
  const n = Number.parseInt(raw, 10);
  if (!Number.isFinite(n) || n < min) {
    throw new Error(`Umgebungsvariable ${name} muss eine Ganzzahl >= ${min} sein, war: '${raw}'`);
  }
  return n;
}

export interface Config {
  port: number;
  host: string;
  /** Max. Zeichen im Base64-Payload eines relay. SDP mit vielen Kandidaten braucht Platz. */
  maxPayloadChars: number;
  /** Nachrichten pro Sekunde und Verbindung (Token-Bucket). */
  rateLimitPerSec: number;
  rateLimitBurst: number;
  /** Ein Raum wird geloescht, wenn er so lange leer ist. */
  roomTtlMs: number;
  /** Verbindung ohne `join` wird nach dieser Zeit getrennt. */
  joinTimeoutMs: number;
  /** Ping-Intervall zum Erkennen toter Verbindungen. */
  heartbeatMs: number;
  stunUrls: string[];
  /** Gesetzt → der Server stellt kurzlebige TURN-Credentials aus (RFC-REST-Verfahren). */
  turnUrls: string[];
  turnSecret: string | null;
  turnTtlSec: number;
}

export function loadConfig(): Config {
  const split = (v: string | undefined) =>
    (v ?? "").split(",").map((s) => s.trim()).filter(Boolean);

  return {
    port: envInt("PORT", 8787, 0),
    host: process.env["HOST"] ?? "0.0.0.0",
    maxPayloadChars: envInt("MAX_PAYLOAD_CHARS", 128 * 1024),
    rateLimitPerSec: envInt("RATE_LIMIT_PER_SEC", 30),
    rateLimitBurst: envInt("RATE_LIMIT_BURST", 60),
    roomTtlMs: envInt("ROOM_TTL_MS", 10 * 60 * 1000),
    joinTimeoutMs: envInt("JOIN_TIMEOUT_MS", 15 * 1000),
    heartbeatMs: envInt("HEARTBEAT_MS", 30 * 1000),
    stunUrls: split(process.env["STUN_URLS"]).length
      ? split(process.env["STUN_URLS"])
      : ["stun:stun.l.google.com:19302"],
    turnUrls: split(process.env["TURN_URLS"]),
    turnSecret: process.env["TURN_SECRET"] || null,
    turnTtlSec: envInt("TURN_TTL_SEC", 600),
  };
}
