import { Store } from "../control/store.mjs";
import { ImprovementWorker } from "../control/improve.mjs";
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { randomUUID } from "node:crypto";
const dir = mkdtempSync(join(tmpdir(), "rms-ai-test-")),
  s = new Store(dir),
  device = randomUUID(),
  session = randomUUID(),
  now = new Date().toISOString();
s.run(
  "INSERT INTO devices(id,token_hash,machine,hotel_id,session_id,last_seen,version,created_at) VALUES(?,?,?,?,?,?,?,?)",
  device,
  "test",
  "TEST FIXTURE",
  "test-hotel",
  session,
  now,
  "0.2.0",
  now,
);
s.run("INSERT INTO sessions VALUES(?,?,?)", session, device, "test-hotel");
s.ingest({
  id: randomUUID(),
  deviceId: device,
  sessionId: session,
  hotelId: "test-hotel",
  profileRevision: 1,
  observation: {
    capturedAt: now,
    source: "ocr",
    ocrLanguage: "none",
    errors: ["OCR_MISSING: 한국어 Windows OCR 언어팩 필요"],
    lines: [],
    windows: [],
  },
  events: [],
});
const w = new ImprovementWorker({
  store: s,
  broadcast: () => {},
  enabled: true,
  corePath: resolve(".work/core/RmsLinkCore"),
  codex: "/Users/ys/.local/bin/codex",
});
try {
  const job = w.enqueue(device, true);
  await w.tick();
  const result = s.get("SELECT status,result FROM jobs WHERE id=?", job.id);
  mkdirSync("artifacts", { recursive: true });
  writeFileSync(
    "artifacts/ai-smoke.json",
    JSON.stringify(
      {
        fixtureOnly: true,
        realCodexInvocation: true,
        ...result,
        result: JSON.parse(result.result),
      },
      null,
      2,
    ),
  );
  console.log(JSON.stringify(result));
  if (result.status === "failed") process.exitCode = 1;
} finally {
  await w.stop();
  s.close();
  rmSync(dir, { recursive: true, force: true });
}
