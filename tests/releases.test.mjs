import test from "node:test";
import assert from "node:assert/strict";
import {
  mkdtempSync,
  writeFileSync,
  mkdirSync,
  rmSync,
  readFileSync,
  copyFileSync,
} from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { Store } from "../control/store.mjs";
import { ReleaseSync } from "../control/releases.mjs";
import { digest } from "../control/domain.mjs";

test("automatic release requires current branch success and matching hash, and never overwrites a version", async () => {
  const dir = mkdtempSync(join(tmpdir(), "rms-release-")),
    fixture = join(dir, "fixture");
  mkdirSync(fixture);
  const execute = async (args) => {
    const config = JSON.parse(readFileSync(join(dir, "config.json"), "utf8"));
    if (args[0] === "api") return config.head;
    if (args[1] === "list") return JSON.stringify([config.run]);
    if (args[1] === "download") {
      const target = args[args.indexOf("--dir") + 1];
      for (const file of ["RmsLink.zip", "validation.json"])
        copyFileSync(join(fixture, file), join(target, file));
      return "";
    }
    throw Error("Unexpected GitHub command");
  };
  const store = new Store(join(dir, "store"));
  const sync = new ReleaseSync(store, () => {}, {
    branch: "codex/test",
    execute,
  });
  function fixtureReport({
    id = 1,
    head = "current",
    commit = "current",
    content = "binary",
    reportHash = digest(content),
    version = "0.4.1",
  } = {}) {
    writeFileSync(
      join(dir, "config.json"),
      JSON.stringify({
        head,
        run: {
          databaseId: id,
          headSha: commit,
          headBranch: "codex/test",
          conclusion: "success",
        },
      }),
    );
    writeFileSync(join(fixture, "RmsLink.zip"), content);
    writeFileSync(
      join(fixture, "validation.json"),
      JSON.stringify({
        passed: true,
        runtime: { passed: true },
        platform: "windows-x64",
        commit,
        sha256: reportHash,
        version,
      }),
    );
  }
  try {
    fixtureReport({ commit: "old" });
    await sync.tick();
    assert.equal(store.all("SELECT * FROM releases").length, 0);
    fixtureReport({ reportHash: "0".repeat(64) });
    await sync.tick();
    assert.equal(store.all("SELECT * FROM releases").length, 0);
    fixtureReport();
    await sync.tick();
    assert.equal(store.get("SELECT version FROM releases").version, "0.4.1");
    fixtureReport({ id: 2, content: "changed binary" });
    await sync.tick();
    assert.equal(
      store.get("SELECT sha256 FROM releases").sha256,
      digest("binary"),
    );
    fixtureReport({ id: 3, version: "0.4.2" });
    await sync.tick();
    assert.equal(store.all("SELECT * FROM releases").length, 2);
  } finally {
    await sync.stop();
    store.close();
    rmSync(dir, { recursive: true, force: true });
  }
});
