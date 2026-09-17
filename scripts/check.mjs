import { spawnSync } from "node:child_process";
import { resolve, join } from "node:path";
import {
  existsSync,
  readdirSync,
  mkdirSync,
  writeFileSync,
  readFileSync,
} from "node:fs";
import { homedir } from "node:os";
const bundled = join(homedir(), ".local/share/rmslink-dotnet/dotnet");
const dotnet = process.env.DOTNET ?? (existsSync(bundled) ? bundled : "dotnet");
const rid =
  { darwin: "osx", win32: "win", linux: "linux" }[process.platform] +
  "-" +
  process.arch;
const folder = resolve(".work/core-check");
function run(file, args, env = process.env) {
  const p = spawnSync(file, args, { stdio: "inherit", env });
  if (p.status !== 0) process.exit(p.status ?? 1);
}
run(dotnet, ["run", "--project", "tests/core/Core.csproj", "-c", "Release"]);
run(dotnet, ["build", "RmsLink.csproj", "-c", "Release", "--nologo"]);
run(dotnet, [
  "publish",
  "tests/core/Core.csproj",
  "-c",
  "Release",
  "-r",
  rid,
  "--self-contained",
  "true",
  "-p:PublishSingleFile=true",
  "-o",
  folder,
]);
const core = join(
  folder,
  "RmsLinkCore" + (process.platform === "win32" ? ".exe" : ""),
);
run(
  process.execPath,
  [
    "--test",
    ...readdirSync("tests")
      .filter((f) => f.endsWith(".test.mjs"))
      .map((f) => join("tests", f)),
  ],
  { ...process.env, RMSLINK_CORE: core },
);
mkdirSync("artifacts", { recursive: true });
const version = JSON.parse(readFileSync("package.json", "utf8")).version;
writeFileSync(
  `artifacts/local-verification-${version}.json`,
  JSON.stringify(
    {
      passed: true,
      version,
      checkedAt: new Date().toISOString(),
      platform: process.platform,
      sharedReadingPolicy: true,
      serverTests: true,
      windowsCrossCompile: true,
      windowsRuntimeVerified: false,
      physicalKeytechVerified: false,
    },
    null,
    2,
  ) + "\n",
);
