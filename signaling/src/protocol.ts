/**
 * Nachrichtentypen des Signaling-Protokolls.
 *
 * WICHTIG: Der Server kennt NUR diese Huelle. Das Feld `payload` ist fuer ihn
 * eine undurchsichtige Base64-Zeichenkette — er darf sie niemals dekodieren,
 * interpretieren oder protokollieren. Siehe docs/05-security.md §5.8.
 */

export const PROTOCOL_VERSION = "ac/1";

/** Rolle im Raum. Pro Raum ist jede Rolle hoechstens einmal besetzt. */
export type Role = "host" | "viewer";

// ── Client → Server ──────────────────────────────────────────────────────────

export interface JoinMessage {
  t: "join";
  /** Oeffentliche Haelfte des Pairing-Tickets, Base32/Base64. Max. 128 Zeichen. */
  roomId: string;
  role: Role;
  /** Protokollversion des Clients; der Server lehnt fremde Major-Versionen ab. */
  v?: string;
}

export interface RelayMessage {
  t: "relay";
  /** Base64-kodierter, Ende-zu-Ende-verschluesselter Umschlag. Fuer den Server opak. */
  payload: string;
}

export interface LeaveMessage {
  t: "leave";
}

export type ClientMessage = JoinMessage | RelayMessage | LeaveMessage;

// ── Server → Client ──────────────────────────────────────────────────────────

export interface JoinedMessage {
  t: "joined";
  roomId: string;
  /** true, wenn der Gegenpart bereits im Raum wartet. */
  peerPresent: boolean;
}

export interface PeerJoinedMessage { t: "peer-joined"; role: Role }
export interface PeerLeftMessage   { t: "peer-left";   role: Role }

export interface IceServersMessage {
  t: "ice-servers";
  servers: Array<{ urls: string[]; username?: string; credential?: string }>;
}

export type ErrorCode =
  | "bad-request"
  | "room-full"
  | "role-taken"
  | "rate-limited"
  | "payload-too-large"
  | "unsupported-version"
  | "not-joined";

export interface ErrorMessage {
  t: "error";
  code: ErrorCode;
  message: string;
  /** true, wenn der Server die Verbindung anschliessend schliesst. */
  fatal: boolean;
}

export type ServerMessage =
  | JoinedMessage | PeerJoinedMessage | PeerLeftMessage
  | RelayMessage  | IceServersMessage | ErrorMessage;

// ── Validierung ──────────────────────────────────────────────────────────────

/** roomId: Base32/Base64-URL-Alphabet, damit sie sich gefahrlos loggen laesst. */
const ROOM_ID_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;

export interface ParseResult {
  ok: boolean;
  message?: ClientMessage;
  error?: { code: ErrorCode; reason: string };
}

/**
 * Streng validierendes Parsen. Alles, was nicht exakt der Spezifikation
 * entspricht, wird abgelehnt — ein Signaling-Server ist die einzige von aussen
 * erreichbare Komponente und damit die Angriffsflaeche des Systems.
 */
export function parseClientMessage(raw: string, maxPayloadChars: number): ParseResult {
  let obj: unknown;
  try {
    obj = JSON.parse(raw);
  } catch {
    return { ok: false, error: { code: "bad-request", reason: "kein gueltiges JSON" } };
  }

  if (typeof obj !== "object" || obj === null || Array.isArray(obj)) {
    return { ok: false, error: { code: "bad-request", reason: "Objekt erwartet" } };
  }

  const m = obj as Record<string, unknown>;

  switch (m["t"]) {
    case "join": {
      const roomId = m["roomId"];
      const role = m["role"];
      const v = m["v"];
      if (typeof roomId !== "string" || !ROOM_ID_PATTERN.test(roomId)) {
        return { ok: false, error: { code: "bad-request", reason: "ungueltige roomId" } };
      }
      if (role !== "host" && role !== "viewer") {
        return { ok: false, error: { code: "bad-request", reason: "ungueltige role" } };
      }
      if (v !== undefined) {
        if (typeof v !== "string") {
          return { ok: false, error: { code: "bad-request", reason: "ungueltige Version" } };
        }
        // Nur die Major-Version muss uebereinstimmen ("ac/1" vs. "ac/1.3").
        const major = (s: string) => s.split(".")[0];
        if (major(v) !== major(PROTOCOL_VERSION)) {
          return {
            ok: false,
            error: { code: "unsupported-version", reason: `Server spricht ${PROTOCOL_VERSION}` },
          };
        }
      }
      const msg: JoinMessage = { t: "join", roomId, role };
      if (typeof v === "string") msg.v = v;
      return { ok: true, message: msg };
    }

    case "relay": {
      const payload = m["payload"];
      if (typeof payload !== "string") {
        return { ok: false, error: { code: "bad-request", reason: "payload muss ein String sein" } };
      }
      if (payload.length > maxPayloadChars) {
        return {
          ok: false,
          error: { code: "payload-too-large", reason: `max. ${maxPayloadChars} Zeichen` },
        };
      }
      return { ok: true, message: { t: "relay", payload } };
    }

    case "leave":
      return { ok: true, message: { t: "leave" } };

    default:
      return { ok: false, error: { code: "bad-request", reason: `unbekannter Typ '${String(m["t"])}'` } };
  }
}
