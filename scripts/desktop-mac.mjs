import {
  mkdirSync,
  writeFileSync,
  readFileSync,
  existsSync,
  copyFileSync,
  mkdtempSync,
  rmSync,
} from "node:fs";
import { join, resolve } from "node:path";
import { homedir } from "node:os";
import { spawnSync } from "node:child_process";
const root = resolve("."),
  desktop = join(homedir(), "Desktop"),
  data = join(homedir(), "Library/Application Support/RmsLink");
const app = join(desktop, "RmsLink 대시보드.app");
function run(command, args) {
  const result = spawnSync(command, args, { encoding: "utf8" });
  if (result.status !== 0) throw Error(result.stderr || command + " failed");
}
const temp = mkdtempSync(join(root, ".work/desktop-"));
try {
  const apple = join(temp, "open.applescript");
  writeFileSync(apple, 'open location "http://127.0.0.1:18760/"\n');
  run("/usr/bin/osacompile", ["-o", app, apple]);
  const iconset = join(temp, "Dashboard.iconset");
  mkdirSync(iconset);
  for (const [name, size] of [
    ["16x16", 16],
    ["16x16@2x", 32],
    ["32x32", 32],
    ["32x32@2x", 64],
    ["128x128", 128],
    ["128x128@2x", 256],
    ["256x256", 256],
    ["256x256@2x", 512],
    ["512x512", 512],
    ["512x512@2x", 1024],
  ])
    run("/usr/bin/sips", [
      "-z",
      String(size),
      String(size),
      join(root, "assets/dashboard.png"),
      "--out",
      join(iconset, `icon_${name}.png`),
    ]);
  const icns = join(app, "Contents/Resources/applet.icns");
  run("/usr/bin/iconutil", ["-c", "icns", iconset, "-o", icns]);
  run("/usr/bin/touch", [app]);
  const key = readFileSync(join(data, "dashboard-access.key"), "utf8").trim();
  const url =
    process.env.RMSLINK_DASHBOARD_ORIGIN ?? "https://rms-link.vercel.app";
  writeFileSync(
    join(desktop, "RmsLink 접속 안내.txt"),
    `RmsLink 대시보드\n\n이 컴퓨터: 바탕화면의 RmsLink 대시보드 아이콘을 두 번 클릭하세요.\n외부 접속: ${url}/\n관리자 접속 코드: ${key}\n\n설정·분석 매뉴얼: ${url}/manual.html\n\n외부 접속 코드는 관리 담당자에게만 전달하세요. Windows 설치 파일에는 포함되지 않습니다.\n내부와 외부는 같은 호텔 데이터를 표시합니다. 서버 컴퓨터가 켜져 있고 로그인·인터넷 연결 상태여야 합니다.\n`,
    { mode: 0o600 },
  );
  console.log(
    JSON.stringify({
      desktopApp: app,
      accessGuide: join(desktop, "RmsLink 접속 안내.txt"),
    }),
  );
} finally {
  rmSync(temp, { recursive: true, force: true });
}
