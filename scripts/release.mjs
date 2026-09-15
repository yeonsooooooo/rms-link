import { readFileSync, copyFileSync, existsSync, mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { Store } from "../control/store.mjs";
import { digest } from "../control/domain.mjs";
const artifact = process.argv[2];
if (!artifact)
  throw new Error(
    "Usage: npm run release -- /absolute/validated-artifact-folder",
  );
const report = JSON.parse(
    readFileSync(join(artifact, "validation.json"), "utf8").replace(
      /^\uFEFF/,
      "",
    ),
  ),
  zip = readFileSync(join(artifact, "RmsLink.zip"));
if (
  report.passed !== true ||
  report.runtime?.passed !== true ||
  report.platform !== "windows-x64" ||
  report.sha256 !== digest(zip) ||
  !/^\d+\.\d+\.\d+$/.test(report.version)
)
  throw new Error("Windows 검증 보고서와 패키지가 일치하지 않습니다.");
const s = new Store(resolve(process.env.RMSLINK_DATA_DIR ?? ".data"));
try {
  const existing = s.get(
    "SELECT sha256 FROM releases WHERE version=?",
    report.version,
  );
  if (existing && existing.sha256 !== report.sha256)
    throw new Error(
      "같은 버전을 다른 패키지로 덮어쓸 수 없습니다. 버전을 올려 주세요.",
    );
  copyFileSync(
    join(artifact, "RmsLink.zip"),
    join(s.dir, "packages", report.sha256 + ".zip"),
  );
  s.run(
    "INSERT OR IGNORE INTO releases VALUES(?,?,?,?,?)",
    report.version,
    report.sha256,
    zip.length,
    "active",
    new Date().toISOString(),
  );
  s.audit("app_release_published", "", {
    version: report.version,
    sha256: report.sha256,
    commit: report.commit,
  });
  console.log(
    JSON.stringify({
      published: true,
      version: report.version,
      sha256: report.sha256,
    }),
  );
} finally {
  s.close();
}
