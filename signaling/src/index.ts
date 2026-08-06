import { loadConfig } from "./config.js";
import { log } from "./logger.js";
import { startServer } from "./server.js";

const cfg = loadConfig();

const server = await startServer(cfg);

const shutdown = async (signal: string) => {
  log.info("Beende Server", { signal });
  await server.close();
  process.exit(0);
};

process.on("SIGINT", () => void shutdown("SIGINT"));
process.on("SIGTERM", () => void shutdown("SIGTERM"));
