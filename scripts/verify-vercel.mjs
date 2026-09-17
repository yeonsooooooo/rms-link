import assert from "node:assert/strict";
import { readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

// Run on the operating server. Credentials stay in memory and are never logged.
const directory =
  process.env.RMSLINK_DATA_DIR ??
  join(homedir(), "Library/Application Support/RmsLink");
const localOrigin =
  process.env.RMSLINK_LOCAL_ORIGIN ?? "http://127.0.0.1:18760";
const adminToken = readFileSync(join(directory, "admin.token"), "utf8").trim();
const localResponse = await fetch(localOrigin + "/api/state", {
  headers: { Authorization: `Bearer ${adminToken}` },
});
assert.equal(localResponse.status, 200);
const local = await localResponse.json();
const origin =
  process.env.RMSLINK_DASHBOARD_ORIGIN ?? local.access.publicOrigin;
assert.equal(new URL(origin).protocol, "https:");
let cookie;
const api = (path, { body, ...options } = {}) =>
  fetch(origin + path, {
    signal: AbortSignal.timeout(25000),
    redirect: "manual",
    method: body === undefined ? "GET" : "POST",
    ...options,
    headers: {
      Origin: origin,
      "X-Rmslink-Client": "1",
      "Content-Type": "application/json",
      ...(cookie ? { Cookie: cookie } : {}),
      ...options.headers,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
const result = { origin, checkedAt: new Date().toISOString(), passed: false };
try {
  assert.equal((await api("/health")).status, 200);
  const home = await api("/");
  assert.equal(home.status, 200);
  assert.match(await home.text(), /RmsLink/);
  const bootstrap = await api("/api/bootstrap", { body: {} });
  assert.equal((await bootstrap.json()).loginRequired, true);
  assert.equal(bootstrap.headers.get("set-cookie"), null);
  for (const path of [
    "/api/state",
    "/api/events",
    "/api/installer",
    "/api/evidence/00000000-0000-4000-8000-000000000000",
  ])
    assert.equal((await api(path)).status, 401);
  const login = await api("/api/login", {
    body: {
      password: readFileSync(
        join(directory, "dashboard-access.key"),
        "utf8",
      ).trim(),
    },
  });
  assert.equal(login.status, 200);
  const setCookie = login.headers.get("set-cookie");
  for (const flag of ["HttpOnly", "Secure", "SameSite=Strict"])
    assert(setCookie.includes(flag));
  assert(!/Domain=/i.test(setCookie));
  cookie = setCookie.split(";")[0];
  result.login = true;
  const stateResponse = await api("/api/state");
  assert.equal(stateResponse.status, 200);
  assert.match(stateResponse.headers.get("cache-control"), /no-store/);
  assert.notEqual(stateResponse.headers.get("x-vercel-cache"), "HIT");
  const remote = await stateResponse.json();
  for (const key of [
    "devices",
    "hotels",
    "rooms",
    "events",
    "jobs",
    "profiles",
    "releases",
  ])
    assert.deepEqual(remote[key], local[key], `Shared ${key}`);
  assert.deepEqual(remote.installer, local.installer, "Shared installer");
  assert.deepEqual(
    remote.capabilities,
    local.capabilities,
    "Shared capabilities",
  );
  result.installerVersion = remote.installer?.version ?? null;
  result.installerWindowsVerified = remote.installer?.windowsVerified === true;
  result.readingMethods = remote.capabilities.includes("reading-methods-v1");
  assert.equal(remote.access.remote, true);
  assert.equal(remote.access.publicOrigin, origin);
  assert.equal((await api("/api/access")).status, 403);
  assert.equal(
    (
      await api("/api/state", {
        headers: { Origin: "https://untrusted.vercel.app" },
      })
    ).status,
    403,
  );
  result.sameData = true;
  result.deviceCount = remote.devices.length;
  result.uncached = true;
  const shortcut = await (await api("/api/shortcut")).text();
  assert.equal(shortcut, `[InternetShortcut]\r\nURL=${origin}/\r\n`);
  assert((await (await api("/manual.html")).text()).includes(origin));
  result.manualAndShortcut = true;
  const link = await (await api("/api/installer-link")).json();
  const installer = await api("/api/installer");
  assert.equal(installer.status, 302);
  assert.equal(installer.headers.get("location"), link.url);
  assert.equal(new URL(link.url).protocol, "https:");
  const download = await fetch(link.url, {
    signal: AbortSignal.timeout(25000),
  });
  assert.equal(download.status, 200);
  assert(Number(download.headers.get("content-length")) > 1000000);
  const downloadReader = download.body.getReader();
  try {
    const { value } = await downloadReader.read();
    assert.equal(Buffer.from(value).subarray(0, 2).toString(), "MZ");
  } finally {
    await downloadReader.cancel();
  }
  result.installerDownload = true;
  const events = await api("/api/events");
  assert.equal(events.status, 200);
  assert.match(events.headers.get("content-type"), /text\/event-stream/);
  const reader = events.body.getReader();
  let stream = "";
  try {
    while (!stream.includes("event: change")) {
      const { value, done } = await reader.read();
      assert(!done, "SSE closed before heartbeat");
      stream += new TextDecoder().decode(value);
    }
    assert(stream.includes("event: ready"));
  } finally {
    await reader.cancel();
  }
  result.liveEvents = true;
  const next = await (await api("/api/state")).json();
  assert(Date.parse(next.at) > Date.parse(remote.at));
  await api("/api/logout", { body: {} });
  assert.equal((await api("/api/state")).status, 401);
  result.logout = true;
  result.passed = true;
  mkdirSync("artifacts", { recursive: true });
  writeFileSync(
    "artifacts/vercel-verification.json",
    JSON.stringify(result, null, 2) + "\n",
  );
  console.log(JSON.stringify(result, null, 2));
} finally {
  if (cookie) await api("/api/logout", { body: {} }).catch(() => {});
}
