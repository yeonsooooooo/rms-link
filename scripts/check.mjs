import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
const dotnet =
  process.env.DOTNET ?? "/Users/ys/.local/share/rmslink-dotnet/dotnet";
for (const args of [
  ["run", "--project", "tests/core/Core.csproj", "-c", "Release"],
  ["build", "RmsLink.csproj", "-c", "Release", "--nologo"],
]) {
  const p = spawnSync(dotnet, args, { stdio: "inherit" });
  if (p.status !== 0) process.exit(p.status ?? 1);
}
const p = spawnSync(process.execPath, ["--test", "tests/control.test.mjs"], {
  stdio: "inherit",
  env: { ...process.env, RMSLINK_CORE: resolve(".work/core/RmsLinkCore") },
});
process.exit(p.status ?? 1);
