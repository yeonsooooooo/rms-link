import {
  mkdirSync,
  writeFileSync,
  readFileSync,
  existsSync,
  copyFileSync,
} from "node:fs";
import { join, resolve } from "node:path";
import { homedir } from "node:os";
import { spawnSync } from "node:child_process";
const root = resolve("."),
  home = homedir(),
  dir = join(home, "Library/Application Support/RmsLink");
mkdirSync(dir, { recursive: true, mode: 0o700 });
mkdirSync(join(dir, "logs"), { recursive: true });
const xml = (s) =>
  s.replace(
    /[<>&"]/g,
    (c) => ({ "<": "&lt;", ">": "&gt;", "&": "&amp;", '"': "&quot;" })[c],
  );
const fields = {
  RMSLINK_DATA_DIR: dir,
  RMSLINK_CORE: join(root, ".work/core/RmsLinkCore"),
  RMSLINK_CODEX: join(home, ".local/bin/codex"),
  PATH:
    join(home, ".local/node/bin") +
    ":" +
    join(home, ".local/bin") +
    ":/usr/bin:/bin:/usr/sbin:/sbin",
  RMSLINK_AUTO_ANALYZE: "true",
  RMSLINK_AUTO_RELEASE: "true",
  RMSLINK_PUBLIC_ORIGIN:
    process.env.RMSLINK_PUBLIC_ORIGIN ??
    "https://ys-macmini.tail984bfd.ts.net:8443",
  RMSLINK_DASHBOARD_ORIGIN:
    process.env.RMSLINK_DASHBOARD_ORIGIN ?? "https://rms-link.vercel.app",
};
const label = "kr.co.rosegold.rmslink-control";
const plist = join(home, "Library/LaunchAgents", label + ".plist");
writeFileSync(
  plist,
  `<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>Label</key><string>${label}</string><key>ProgramArguments</key><array><string>${xml(process.execPath)}</string><string>${xml(join(root, "control/index.mjs"))}</string></array><key>WorkingDirectory</key><string>${xml(root)}</string><key>EnvironmentVariables</key><dict>${Object.entries(
    fields,
  )
    .map(([k, v]) => `<key>${k}</key><string>${xml(v)}</string>`)
    .join(
      "",
    )}</dict><key>RunAtLoad</key><true/><key>KeepAlive</key><true/><key>ThrottleInterval</key><integer>10</integer><key>Umask</key><integer>63</integer><key>StandardOutPath</key><string>${xml(join(dir, "logs/server.log"))}</string><key>StandardErrorPath</key><string>${xml(join(dir, "logs/server.error.log"))}</string></dict></plist>`,
);
spawnSync("launchctl", ["bootout", `gui/${process.getuid()}/${label}`], {
  stdio: "ignore",
});
await new Promise((r) => setTimeout(r, 1500));
const result = spawnSync(
  "launchctl",
  ["bootstrap", `gui/${process.getuid()}`, plist],
  { stdio: "inherit" },
);
if (result.status) process.exit(result.status);
console.log("RmsLink service installed: http://127.0.0.1:18760");
