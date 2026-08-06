import { createServer, type IncomingMessage, type Server as HttpServer } from "node:http";
import { randomUUID } from "node:crypto";
import { WebSocketServer, WebSocket } from "ws";

import { type Config } from "./config.js";
import { log } from "./logger.js";
import { parseClientMessage, PROTOCOL_VERSION, type ErrorCode, type Role, type ServerMessage } from "./protocol.js";
import { RoomRegistry, type Participant } from "./rooms.js";
import { TokenBucket } from "./rateLimit.js";
import { makeIceServers } from "./turn.js";

interface ConnectionState {
  readonly id: string;
  roomId: string | null;
  role: Role | null;
  bucket: TokenBucket;
  alive: boolean;
  joinTimer: NodeJS.Timeout | null;
}

export interface RunningServer {
  http: HttpServer;
  wss: WebSocketServer;
  registry: RoomRegistry;
  /** Tatsaechlich gebundener Port (relevant bei port=0 in Tests). */
  port: number;
  close(): Promise<void>;
}

/**
 * Startet den Rendezvous-Server.
 *
 * Die gesamte Logik passt in eine Funktion, und das ist Absicht: Je weniger dieser
 * Server tut, desto weniger kann er falsch machen und desto weniger muss man ihm
 * vertrauen. Er kennt keine Nutzer, keine Sitzungen und keine Inhalte — er merkt
 * sich nur, welche zwei Sockets zusammengehoeren.
 */
export function startServer(cfg: Config): Promise<RunningServer> {
  const registry = new RoomRegistry(cfg.roomTtlMs);

  const http = createServer((req, res) => {
    if (req.url === "/health" || req.url === "/") {
      res.writeHead(200, { "content-type": "application/json" });
      res.end(JSON.stringify({
        ok: true,
        service: "appcontrol-signaling",
        protocol: PROTOCOL_VERSION,
        ...registry.stats(),
      }));
      return;
    }
    res.writeHead(404).end();
  });

  const wss = new WebSocketServer({ server: http, path: "/ws", maxPayload: cfg.maxPayloadChars + 4096 });

  wss.on("connection", (ws: WebSocket, req: IncomingMessage) => {
    const state: ConnectionState = {
      id: randomUUID(),
      roomId: null,
      role: null,
      bucket: new TokenBucket(cfg.rateLimitPerSec, cfg.rateLimitBurst),
      alive: true,
      joinTimer: null,
    };

    const send = (msg: ServerMessage) => {
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
    };

    const fail = (code: ErrorCode, message: string, fatal: boolean) => {
      send({ t: "error", code, message, fatal });
      if (fatal) ws.close(1008, code);
    };

    const participant: Participant = {
      id: state.id,
      // `role` ist zum Zeitpunkt der Objekterzeugung noch null; nach dem join steht
      // sie fest. Der Getter liest sie erst beim Zugriff aus dem State.
      get role(): Role { return state.role!; },
      send,
      close: (code, reason) => ws.close(code ?? 1000, reason),
    };

    // Verbindungen, die nicht innerhalb der Frist beitreten, sind entweder Scanner
    // oder kaputt. Beides braucht keine offenen Sockets zu belegen.
    state.joinTimer = setTimeout(() => {
      if (state.roomId === null) {
        log.debug("join-Timeout", { conn: state.id });
        ws.close(1008, "join-timeout");
      }
    }, cfg.joinTimeoutMs);

    ws.on("pong", () => { state.alive = true; });

    ws.on("message", (data, isBinary) => {
      if (isBinary) {
        fail("bad-request", "Binaerframes werden nicht unterstuetzt", true);
        return;
      }

      if (!state.bucket.tryConsume()) {
        fail("rate-limited", "zu viele Nachrichten", false);
        return;
      }

      const parsed = parseClientMessage(data.toString(), cfg.maxPayloadChars);
      if (!parsed.ok || !parsed.message) {
        const e = parsed.error!;
        // Ein Protokollfehler VOR dem join deutet auf einen falschen Client hin →
        // trennen. Danach reicht eine Fehlermeldung, ohne die Sitzung zu zerstoeren.
        fail(e.code, e.reason, state.roomId === null);
        return;
      }

      const msg = parsed.message;

      switch (msg.t) {
        case "join": {
          if (state.roomId !== null) {
            fail("bad-request", "bereits einem Raum beigetreten", false);
            return;
          }
          state.role = msg.role;
          const result = registry.join(msg.roomId, participant);
          if (!result.ok) {
            state.role = null;
            fail(result.code, result.reason, true);
            return;
          }
          state.roomId = msg.roomId;
          if (state.joinTimer) { clearTimeout(state.joinTimer); state.joinTimer = null; }

          send({ t: "joined", roomId: msg.roomId, peerPresent: result.peerPresent });
          send({ t: "ice-servers", servers: makeIceServers(cfg, msg.role) });

          // Beide Seiten muessen erfahren, dass der andere da ist — der zuerst
          // Wartende bekommt `peer-joined`, der Nachzuegler `peerPresent: true`.
          const peer = registry.peerOf(msg.roomId, participant);
          if (peer) peer.send({ t: "peer-joined", role: msg.role });
          return;
        }

        case "relay": {
          if (state.roomId === null) {
            fail("not-joined", "erst 'join' senden", false);
            return;
          }
          const peer = registry.peerOf(state.roomId, participant);
          if (!peer) {
            // Kein Fehler: Der Gegenpart ist evtl. noch nicht da. Der Client
            // wiederholt nach `peer-joined`. Stilles Verwerfen ist hier korrekt.
            log.debug("relay ohne Gegenpart verworfen", { conn: state.id });
            return;
          }
          // Weiterleiten ohne Ansehen des Inhalts. Genau hier endet das Wissen
          // des Servers ueber die Sitzung.
          peer.send({ t: "relay", payload: msg.payload });
          return;
        }

        case "leave": {
          if (state.roomId !== null) registry.leave(state.roomId, participant);
          ws.close(1000, "bye");
          return;
        }
      }
    });

    ws.on("close", () => {
      if (state.joinTimer) clearTimeout(state.joinTimer);
      if (state.roomId !== null) registry.leave(state.roomId, participant);
    });

    ws.on("error", (err) => {
      log.warn("Socket-Fehler", { conn: state.id, err: String(err) });
    });

    log.debug("Verbindung geoeffnet", {
      conn: state.id,
      // Nur bei LOG_LEVEL=debug und nur die Herkunft, nie Inhalte.
      remote: req.socket.remoteAddress,
    });
  });

  // Heartbeat: haengende Sockets (NAT-Timeout, Schlafmodus) erkennen und schliessen,
  // damit ihr Raumplatz frei wird.
  const heartbeat = setInterval(() => {
    for (const client of wss.clients) {
      const ws = client as WebSocket & { __acAlive?: boolean };
      if (ws.readyState !== WebSocket.OPEN) continue;
      if (ws.__acAlive === false) { ws.terminate(); continue; }
      ws.__acAlive = false;
      ws.ping();
      ws.once("pong", () => { ws.__acAlive = true; });
    }
  }, cfg.heartbeatMs);

  const sweeper = setInterval(() => registry.sweep(), Math.max(30_000, cfg.roomTtlMs / 4));

  return new Promise((resolve, reject) => {
    http.once("error", reject);
    http.listen(cfg.port, cfg.host, () => {
      const addr = http.address();
      const port = typeof addr === "object" && addr ? addr.port : cfg.port;
      log.info("Signaling-Server laeuft", { host: cfg.host, port, protocol: PROTOCOL_VERSION });

      resolve({
        http, wss, registry, port,
        close: () =>
          new Promise<void>((done) => {
            clearInterval(heartbeat);
            clearInterval(sweeper);
            for (const c of wss.clients) c.terminate();
            wss.close(() => http.close(() => done()));
          }),
      });
    });
  });
}
