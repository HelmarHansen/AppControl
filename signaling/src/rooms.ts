import type { Role, ServerMessage } from "./protocol.js";
import { log, redactRoom } from "./logger.js";

/**
 * Ein Teilnehmer im Raum. Bewusst als Interface statt an den WebSocket gebunden,
 * damit `RoomRegistry` ohne Netzwerk testbar bleibt.
 */
export interface Participant {
  readonly id: string;
  readonly role: Role;
  send(msg: ServerMessage): void;
  close(code?: number, reason?: string): void;
}

export interface Room {
  readonly id: string;
  host: Participant | null;
  viewer: Participant | null;
  /** Zeitpunkt, ab dem der Raum leer ist. null = besetzt. */
  emptySince: number | null;
  createdAt: number;
}

export type JoinResult =
  | { ok: true; room: Room; peerPresent: boolean }
  | { ok: false; code: "room-full" | "role-taken"; reason: string };

/**
 * Verwaltung aller Raeume — reiner In-Memory-Zustand.
 *
 * Bewusst KEINE Persistenz: Ein Raum ist ein fluechtiges Rendezvous zwischen genau
 * zwei Teilnehmern. Wird der Server neu gestartet, verbinden sich beide Seiten neu.
 * Nichts an diesem Zustand ist es wert, eine Datenbank zu betreiben — und alles,
 * was nicht gespeichert wird, kann auch nicht beschlagnahmt oder geleakt werden.
 */
export class RoomRegistry {
  private readonly rooms = new Map<string, Room>();

  constructor(private readonly roomTtlMs: number) {}

  get size(): number { return this.rooms.size; }

  join(roomId: string, participant: Participant): JoinResult {
    let room = this.rooms.get(roomId);
    if (!room) {
      room = { id: roomId, host: null, viewer: null, emptySince: null, createdAt: Date.now() };
      this.rooms.set(roomId, room);
      log.debug("Raum erstellt", { room: redactRoom(roomId) });
    }

    const slot = participant.role;
    if (room[slot] !== null) {
      return {
        ok: false,
        code: "role-taken",
        reason: `Rolle '${slot}' ist in diesem Raum bereits belegt`,
      };
    }

    // Zweite Sicherung: Nie mehr als zwei Teilnehmer. Waere `role-taken` je
    // umgangen, faengt das hier.
    const occupied = (room.host ? 1 : 0) + (room.viewer ? 1 : 0);
    if (occupied >= 2) {
      return { ok: false, code: "room-full", reason: "Raum ist voll (max. 2 Teilnehmer)" };
    }

    const other = slot === "host" ? room.viewer : room.host;
    room[slot] = participant;
    room.emptySince = null;

    log.info("beigetreten", {
      room: redactRoom(roomId), role: slot, peerPresent: other !== null,
    });

    return { ok: true, room, peerPresent: other !== null };
  }

  /** Gegenpart eines Teilnehmers, oder null. */
  peerOf(roomId: string, participant: Participant): Participant | null {
    const room = this.rooms.get(roomId);
    if (!room) return null;
    const other = participant.role === "host" ? room.viewer : room.host;
    // Identitaetspruefung: schuetzt gegen Weiterleitung an einen bereits ersetzten Slot.
    return other && other.id !== participant.id ? other : null;
  }

  leave(roomId: string, participant: Participant): void {
    const room = this.rooms.get(roomId);
    if (!room) return;

    const slot = participant.role;
    // Nur raeumen, wenn es wirklich noch DIESER Teilnehmer ist — sonst wuerde ein
    // verspaetetes `close` eines alten Sockets den Nachfolger aus dem Raum werfen.
    if (room[slot]?.id !== participant.id) return;
    room[slot] = null;

    const other = slot === "host" ? room.viewer : room.host;
    if (other) {
      other.send({ t: "peer-left", role: slot });
    } else {
      room.emptySince = Date.now();
    }

    log.info("verlassen", { room: redactRoom(roomId), role: slot });
  }

  /**
   * Entfernt Raeume, die laenger als `roomTtlMs` leer sind.
   * Gibt die Zahl der entfernten Raeume zurueck.
   */
  sweep(now: number = Date.now()): number {
    let removed = 0;
    for (const [id, room] of this.rooms) {
      if (room.emptySince !== null && now - room.emptySince > this.roomTtlMs) {
        this.rooms.delete(id);
        removed++;
      }
    }
    if (removed > 0) log.debug("Raeume aufgeraeumt", { removed, remaining: this.rooms.size });
    return removed;
  }

  /** Nur fuer den /health-Endpunkt — enthaelt bewusst keine Raum-IDs. */
  stats() {
    let occupied = 0;
    let paired = 0;
    for (const room of this.rooms.values()) {
      const n = (room.host ? 1 : 0) + (room.viewer ? 1 : 0);
      if (n > 0) occupied++;
      if (n === 2) paired++;
    }
    return { rooms: this.rooms.size, occupied, paired };
  }
}
