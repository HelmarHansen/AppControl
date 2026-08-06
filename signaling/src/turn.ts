import { createHmac } from "node:crypto";
import type { Config } from "./config.js";

export interface IceServer {
  urls: string[];
  username?: string;
  credential?: string;
}

/**
 * Kurzlebige TURN-Zugangsdaten nach dem "REST API For Access To TURN Services"-Verfahren
 * (draft-uberti-behave-turn-rest), das coturn mit `use-auth-secret` unterstuetzt.
 *
 *   username   = <unix-ablaufzeit>:<beliebiger bezeichner>
 *   credential = base64(HMAC-SHA1(secret, username))
 *
 * Der Vorteil gegenueber statischen Zugangsdaten: In der ausgelieferten App steht
 * kein Passwort, und geleakte Credentials sind nach `turnTtlSec` wertlos.
 */
export function makeIceServers(cfg: Config, label: string): IceServer[] {
  const servers: IceServer[] = [{ urls: cfg.stunUrls }];

  if (cfg.turnUrls.length > 0 && cfg.turnSecret) {
    const expiry = Math.floor(Date.now() / 1000) + cfg.turnTtlSec;
    // Bezeichner auf harmlose Zeichen begrenzen — er landet in coturns Logs.
    const safeLabel = label.replace(/[^A-Za-z0-9_-]/g, "").slice(0, 32) || "ac";
    const username = `${expiry}:${safeLabel}`;
    const credential = createHmac("sha1", cfg.turnSecret).update(username).digest("base64");
    servers.push({ urls: cfg.turnUrls, username, credential });
  }

  return servers;
}
