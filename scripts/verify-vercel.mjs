import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
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
const entryOrigin =
  process.env.RMSLINK_DASHBOARD_ORIGIN ?? local.access.publicOrigin;
assert.equal(new URL(entryOrigin).protocol, "https:");
const routing = JSON.parse(
  readFileSync(
    new URL("../deploy/vercel/vercel.json", import.meta.url),
    "utf8",
  ),
);
const expectedTarget = routing.redirects?.find(
  (route) => route.source === "/",
)?.destination;
const landing = await fetch(entryOrigin + "/", {
  redirect: "manual",
  signal: AbortSignal.timeout(25000),
});
if (expectedTarget && entryOrigin === local.access.publicOrigin)
  assert.equal(landing.status, 307, "Public dashboard entry redirect");
let origin = entryOrigin;
if (landing.status === 307) {
  assert.equal(landing.headers.get("location"), expectedTarget);
  origin = new URL(expectedTarget).origin;
} else assert.equal(landing.status, 200);
await landing.body?.cancel();
let cookie;
const request = (path, { body, ...options } = {}) =>
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
const readRetries = [];
const api = async (path, options = {}) => {
  for (let attempt = 0; ; attempt++) {
    const response = await request(path, options);
    if (
      options.body === undefined &&
      (!options.method || options.method === "GET") &&
      [502, 503, 504].includes(response.status) &&
      attempt < 2
    ) {
      readRetries.push({ path, status: response.status });
      await response.body?.cancel();
      await new Promise((resolve) => setTimeout(resolve, 500 * (attempt + 1)));
      continue;
    }
    return response;
  }
};
const result = {
  entryOrigin,
  origin,
  entryRedirect: origin !== entryOrigin,
  checkedAt: new Date().toISOString(),
  passed: false,
};
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
  assert.equal(remote.access.publicOrigin, local.access.publicOrigin);
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
  assert.equal(
    shortcut,
    `[InternetShortcut]\r\nURL=${local.access.publicOrigin}/\r\n`,
  );
  const manual = await api("/manual.html");
  assert.equal(manual.status, 200);
  assert((await manual.text()).includes(local.access.publicOrigin));
  result.manualAndShortcut = true;
  const grantResponse = await api("/api/enrollment", { body: {} });
  assert.equal(grantResponse.status, 201);
  const grant = await grantResponse.json();
  try {
    assert.match(grant.code, /^[a-f0-9]{48}$/);
    assert(Date.parse(grant.expiresAt) > Date.now());
    assert.equal(grant.remaining, 100);
    result.enrollmentRecovery = true;
  } finally {
    // This operator verification runs on the same server; remove only its unused test grant.
    const db = new DatabaseSync(join(directory, "state.db"));
    try {
      db.prepare("DELETE FROM enrollments WHERE hash=?").run(
        createHash("sha256").update(grant.code).digest("hex"),
      );
    } finally {
      db.close();
    }
  }
  const diagnostic = await api("/api/diagnostic-tool");
  assert.equal(diagnostic.status, 200);
  assert.match(
    diagnostic.headers.get("content-disposition"),
    /RmsLink-Diagnostics.zip/,
  );
  assert.deepEqual(
    Buffer.from(await diagnostic.arrayBuffer()),
    readFileSync(new URL("../assets/RmsLink-Diagnostics.zip", import.meta.url)),
  );
  result.independentDiagnostics = true;
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
  result.readRetries = readRetries;
  mkdirSync("artifacts", { recursive: true });
  writeFileSync(
    "artifacts/vercel-verification.json",
    JSON.stringify(result, null, 2) + "\n",
  );
  console.log(JSON.stringify(result, null, 2));
} finally {
  if (cookie) await api("/api/logout", { body: {} }).catch(() => {});
}
