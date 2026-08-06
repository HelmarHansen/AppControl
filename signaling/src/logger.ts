/**
 * Bewusst minimales Logging.
 *
 * Der Server darf NIEMALS protokollieren:
 *   - relay-Payloads (auch nicht gekuerzt)
 *   - vollstaendige Raum-IDs (sie sind die halbe Pairing-Information)
 *
 * Geloggt wird nur, was zum Betrieb noetig ist. Wer noch weniger will, setzt
 * LOG_LEVEL=error.
 */

type Level = "debug" | "info" | "warn" | "error";
const ORDER: Record<Level, number> = { debug: 10, info: 20, warn: 30, error: 40 };

/**
 * Schwelle wird bei jedem Aufruf gelesen statt beim Import. Kostet einen
 * Map-Lookup und erlaubt es, LOG_LEVEL zur Laufzeit zu setzen — was Tests
 * brauchen, die den Server im selben Prozess hochfahren.
 */
function threshold(): number {
  const configured = (process.env["LOG_LEVEL"] ?? "info") as Level;
  return ORDER[configured] ?? ORDER.info;
}

/** Kuerzt eine Raum-ID auf ein nicht rekonstruierbares Praefix fuer die Korrelation. */
export function redactRoom(roomId: string): string {
  return roomId.slice(0, 6) + "…";
}

function emit(level: Level, msg: string, fields?: Record<string, unknown>) {
  if (ORDER[level] < threshold()) return;
  const line = { ts: new Date().toISOString(), level, msg, ...fields };
  const out = level === "error" || level === "warn" ? process.stderr : process.stdout;
  out.write(JSON.stringify(line) + "\n");
}

export const log = {
  debug: (m: string, f?: Record<string, unknown>) => emit("debug", m, f),
  info:  (m: string, f?: Record<string, unknown>) => emit("info", m, f),
  warn:  (m: string, f?: Record<string, unknown>) => emit("warn", m, f),
  error: (m: string, f?: Record<string, unknown>) => emit("error", m, f),
};
