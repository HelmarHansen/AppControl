import { test } from "node:test";
import assert from "node:assert/strict";
import { WebSocket } from "ws";

import { startServer, type RunningServer } from "../src/server.js";
import { loadConfig } from "../src/config.js";
import type { ServerMessage } from "../src/protocol.js";

/** Testclient mit await-barer Nachrichten-Warteschlange. */
class TestClient {
  private queue: ServerMessage[] = [];
  private waiters: Array<(m: ServerMessage) => void> = [];
  readonly ws: WebSocket;

  private constructor(url: string) {
    this.ws = new WebSocket(url);
    this.ws.on("message", (d) => {
      const msg = JSON.parse(d.toString()) as ServerMessage;
      const w = this.waiters.shift();
      if (w) w(msg); else this.queue.push(msg);
    });
  }

  static async connect(port: number): Promise<TestClient> {
    const c = new TestClient(`ws://127.0.0.1:${port}/ws`);
    await new Promise<void>((res, rej) => {
      c.ws.once("open", res);
      c.ws.once("error", rej);
    });
    return c;
  }

  send(msg: unknown) { this.ws.send(JSON.stringify(msg)); }

  next(timeoutMs = 2000): Promise<ServerMessage> {
    const queued = this.queue.shift();
    if (queued) return Promise.resolve(queued);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Timeout beim Warten auf Nachricht")), timeoutMs);
      this.waiters.push((m) => { clearTimeout(timer); resolve(m); });
    });
  }

  /** Wartet auf die erste Nachricht eines bestimmten Typs, ueberspringt andere. */
  async nextOf(t: string, timeoutMs = 2000): Promise<ServerMessage> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const m = await this.next(Math.max(50, deadline - Date.now()));
      if (m.t === t) return m;
    }
  }

  close() { this.ws.close(); }
}

async function withServer(fn: (s: RunningServer) => Promise<void>, overrides: Record<string, string> = {}) {
  const saved = { ...process.env };
  Object.assign(process.env, { PORT: "0", LOG_LEVEL: "error", ...overrides });
  const server = await startServer(loadConfig());
  try {
    await fn(server);
  } finally {
    await server.close();
    process.env = saved;
  }
}

const ROOM = "TESTROOM12345678";

test("join liefert joined + ice-servers", async () => {
  await withServer(async (s) => {
    const host = await TestClient.connect(s.port);
    host.send({ t: "join", roomId: ROOM, role: "host" });

    const joined = await host.next();
    assert.equal(joined.t, "joined");
    assert.equal((joined as any).peerPresent, false);

    const ice = await host.next();
    assert.equal(ice.t, "ice-servers");
    assert.ok(Array.isArray((ice as any).servers) && (ice as any).servers.length >= 1);

    host.close();
  });
});

test("der wartende Teilnehmer wird ueber den Beitritt informiert", async () => {
  await withServer(async (s) => {
    const host = await TestClient.connect(s.port);
    host.send({ t: "join", roomId: ROOM, role: "host" });
    await host.nextOf("ice-servers");

    const viewer = await TestClient.connect(s.port);
    viewer.send({ t: "join", roomId: ROOM, role: "viewer" });

    const notice = await host.nextOf("peer-joined");
    assert.equal((notice as any).role, "viewer");

    const vJoined = await viewer.nextOf("joined");
    assert.equal((vJoined as any).peerPresent, true, "Nachzuegler sieht den Wartenden");

    host.close(); viewer.close();
  });
});

test("relay reicht den Payload bitgenau und unveraendert durch", async () => {
  await withServer(async (s) => {
    const host = await TestClient.connect(s.port);
    const viewer = await TestClient.connect(s.port);
    host.send({ t: "join", roomId: ROOM, role: "host" });
    await host.nextOf("ice-servers");
    viewer.send({ t: "join", roomId: ROOM, role: "viewer" });
    await viewer.nextOf("ice-servers");
    await host.nextOf("peer-joined");

    const payload = "QUMxAAECAAAAAAAAAAAAAAAAAAB2ZXJ5LXNlY3JldA==";
    host.send({ t: "relay", payload });

    const got = await viewer.nextOf("relay");
    assert.equal((got as any).payload, payload);

    // Und in die Gegenrichtung.
    const back = "cmV2ZXJzZS1kaXJlY3Rpb24=";
    viewer.send({ t: "relay", payload: back });
    assert.equal((await host.nextOf("relay") as any).payload, back);

    host.close(); viewer.close();
  });
});

test("relay ohne Gegenpart wird still verworfen, die Verbindung bleibt nutzbar", async () => {
  await withServer(async (s) => {
    const host = await TestClient.connect(s.port);
    host.send({ t: "join", roomId: ROOM, role: "host" });
    await host.nextOf("ice-servers");

    host.send({ t: "relay", payload: "AAAA" });

    // Kein Fehler erwartet — stattdessen muss ein spaeter beitretender Viewer
    // ganz normal ankuendigt werden.
    const viewer = await TestClient.connect(s.port);
    viewer.send({ t: "join", roomId: ROOM, role: "viewer" });
    assert.equal((await host.nextOf("peer-joined")).t, "peer-joined");

    host.close(); viewer.close();
  });
});

test("ein dritter Client wird mit role-taken abgewiesen", async () => {
  await withServer(async (s) => {
    const a = await TestClient.connect(s.port);
    const b = await TestClient.connect(s.port);
    a.send({ t: "join", roomId: ROOM, role: "host" });
    await a.nextOf("ice-servers");
    b.send({ t: "join", roomId: ROOM, role: "viewer" });
    await b.nextOf("ice-servers");

    const c = await TestClient.connect(s.port);
    c.send({ t: "join", roomId: ROOM, role: "viewer" });

    const err = await c.nextOf("error");
    assert.equal((err as any).code, "role-taken");
    assert.equal((err as any).fatal, true);

    a.close(); b.close(); c.close();
  });
});

test("relay vor dem join wird mit not-joined beantwortet", async () => {
  await withServer(async (s) => {
    const c = await TestClient.connect(s.port);
    c.send({ t: "relay", payload: "AAAA" });

    const err = await c.nextOf("error");
    assert.equal((err as any).code, "not-joined");
    c.close();
  });
});

test("Trennung eines Teilnehmers meldet peer-left an den anderen", async () => {
  await withServer(async (s) => {
    const host = await TestClient.connect(s.port);
    const viewer = await TestClient.connect(s.port);
    host.send({ t: "join", roomId: ROOM, role: "host" });
    await host.nextOf("ice-servers");
    viewer.send({ t: "join", roomId: ROOM, role: "viewer" });
    await host.nextOf("peer-joined");

    viewer.close();

    const left = await host.nextOf("peer-left", 3000);
    assert.equal((left as any).role, "viewer");
    host.close();
  });
});

test("Rate-Limit greift und trennt die Verbindung nicht", async () => {
  await withServer(async (s) => {
    const c = await TestClient.connect(s.port);
    c.send({ t: "join", roomId: ROOM, role: "host" });
    await c.nextOf("ice-servers");

    for (let i = 0; i < 20; i++) c.send({ t: "relay", payload: "AAAA" });

    const err = await c.nextOf("error");
    assert.equal((err as any).code, "rate-limited");
    assert.equal((err as any).fatal, false, "Rate-Limit darf die Sitzung nicht abbrechen");
    c.close();
  }, { RATE_LIMIT_PER_SEC: "1", RATE_LIMIT_BURST: "3" });
});

test("/health antwortet ohne Raum-IDs preiszugeben", async () => {
  await withServer(async (s) => {
    const res = await fetch(`http://127.0.0.1:${s.port}/health`);
    assert.equal(res.status, 200);
    const body = await res.json() as any;
    assert.equal(body.ok, true);
    assert.equal(body.protocol, "ac/1");
    assert.ok(typeof body.rooms === "number");
    assert.equal(JSON.stringify(body).includes(ROOM), false);
  });
});
