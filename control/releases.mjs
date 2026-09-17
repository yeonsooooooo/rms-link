import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, copyFileSync } from "node:fs";
import { join } from "node:path";
import { digest } from "./domain.mjs";
const repository = "yeonsooooooo/rms-link";
const versionCompare = (a, b) => {
  const x = a.split(".").map(Number),
    y = b.split(".").map(Number);
  return x[0] - y[0] || x[1] - y[1] || x[2] - y[2];
};
function gh(args) {
  return new Promise((resolve, reject) => {
    const p = spawn(process.env.RMSLINK_GH ?? "/Users/ys/.local/bin/gh", args, {
      stdio: ["ignore", "pipe", "pipe"],
    });
    let text = "",
      error = "";
    const timer = setTimeout(() => p.kill("SIGTERM"), 180000);
    p.stdout.on("data", (d) => (text += d));
    p.stderr.on("data", (d) => (error = (error + d).slice(-2000)));
    p.on("error", (e) => {
      clearTimeout(timer);
      reject(e);
    });
    p.on("close", (c) => {
      clearTimeout(timer);
      c === 0
        ? resolve(text)
        : reject(new Error(error || "GitHub build fetch failed"));
    });
  });
}
export class ReleaseSync {
  constructor(
    store,
    broadcast,
    { enabled = true, branch = "codex/rmslink-control", execute = gh } = {},
  ) {
    this.execute = execute;
    this.store = store;
    this.broadcast = broadcast;
    this.enabled = enabled;
    this.branch = branch;
    this.running = false;
    this.stopped = false;
  }
  start() {
    if (this.enabled) {
      this.timer = setInterval(() => this.tick(), 300000);
      this.timer.unref();
    }
  }
  async tick() {
    if (!this.enabled || this.running || this.stopped) return;
    this.running = true;
    const s = this.store;
    try {
      const runs = JSON.parse(
        await this.execute([
          "run",
          "list",
          "--repo",
          repository,
          "--workflow",
          "build.yml",
          "--branch",
          this.branch,
          "--status",
          "success",
          "--limit",
          "1",
          "--json",
          "databaseId,headSha,headBranch,status,conclusion",
        ]),
      );
      const run = runs[0];
      if (
        !run ||
        run.headBranch !== this.branch ||
        run.conclusion !== "success"
      )
        return;
      const head = (
        await this.execute([
          "api",
          `repos/${repository}/commits/${encodeURIComponent(this.branch)}`,
          "--jq",
          ".sha",
        ])
      ).trim();
      if (run.headSha !== head) return; // Never deploy an older success while the current revision is unverified.
      const key = "release-run-" + run.databaseId;
      if (s.get("SELECT key FROM settings WHERE key=?", key)) return;
      const folder = join(s.dir, "builds", String(run.databaseId));
      mkdirSync(folder, { recursive: true });
      await this.execute([
        "run",
        "download",
        String(run.databaseId),
        "--repo",
        repository,
        "--name",
        "rmslink-windows-validated",
        "--dir",
        folder,
      ]);
      const report = JSON.parse(
        readFileSync(join(folder, "validation.json"), "utf8").replace(
          /^\uFEFF/,
          "",
        ),
      );
      const bytes = readFileSync(join(folder, "RmsLink.zip"));
      if (
        report.passed !== true ||
        report.runtime?.passed !== true ||
        report.platform !== "windows-x64" ||
        report.commit !== run.headSha ||
        report.sha256 !== digest(bytes) ||
        !/^\d+\.\d+\.\d+$/.test(report.version)
      )
        throw new Error("빌드 검증 보고서 불일치");
      const old = s.get(
        "SELECT * FROM releases WHERE version=?",
        report.version,
      );
      if (old && old.sha256 !== report.sha256) {
        s.audit("release_version_conflict", "", {
          version: report.version,
          run: run.databaseId,
          message:
            "같은 버전을 다른 바이너리로 교체하지 않습니다. 버전을 올려 주세요.",
        });
        s.run("INSERT OR REPLACE INTO settings VALUES(?,?)", key, "conflict");
        return;
      }
      const current = s
        .all("SELECT version FROM releases WHERE status='active'")
        .sort((a, b) => versionCompare(b.version, a.version))[0];
      if (!current || versionCompare(report.version, current.version) > 0) {
        copyFileSync(
          join(folder, "RmsLink.zip"),
          join(s.dir, "packages", report.sha256 + ".zip"),
        );
        s.run(
          "INSERT INTO releases VALUES(?,?,?,?,?)",
          report.version,
          report.sha256,
          bytes.length,
          "active",
          new Date().toISOString(),
        );
        s.audit("app_release_published", "", {
          version: report.version,
          run: run.databaseId,
          commit: report.commit,
          sha256: report.sha256,
        });
        this.broadcast();
      }
      s.run("INSERT OR REPLACE INTO settings VALUES(?,?)", key, "processed");
    } catch (e) {
      if (!this.stopped)
        s.audit("release_sync_failed", "", { error: e.message });
    } finally {
      this.running = false;
    }
  }
  async stop() {
    this.stopped = true;
    clearInterval(this.timer);
    while (this.running) await new Promise((r) => setTimeout(r, 200));
  }
}
