import { createApp } from "./app.mjs";
import { resolve } from "node:path";
const app = await createApp({
  directory: resolve(process.env.RMSLINK_DATA_DIR ?? ".data"),
  adminPort: Number(process.env.RMSLINK_ADMIN_PORT ?? 18760),
  agentPort: Number(process.env.RMSLINK_AGENT_PORT ?? 18761),
  autoRelease: process.env.RMSLINK_AUTO_RELEASE === "true",
  autoAnalyze: process.env.RMSLINK_AUTO_ANALYZE !== "false",
});
console.log("RmsLink control: " + app.url);
for (const signal of ["SIGINT", "SIGTERM"])
  process.once(signal, async () => {
    await app.close();
    process.exit(0);
  });
