import test from "node:test";
import { request } from "node:http";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createApp } from "../control/app.mjs";
import { DashboardAuth } from "../control/auth.mjs";
const publicOrigin = "https://rms.example.test";
async function setup(t, options = {}) {
  const directory = mkdtempSync(join(tmpdir(), "rms-access-"));
  const app = await createApp({
    directory,
    publicOrigin,
    adminPort: 0,
    agentPort: 0,
    autoAnalyze: false,
    ...options,
  });
  t.after(async () => {
    await app.close();
    rmSync(directory, { recursive: true, force: true });
  });
  return app;
}
async function remote(app, path, body, cookie, extra = {}) {
  return new Promise((resolve, reject) => {
    const r = request(
      app.agentUrl + path,
      {
        method: body === undefined ? "GET" : "POST",
        headers: {
          Host: "rms.example.test",
          Origin: publicOrigin,
          "X-Rmslink-Client": "1",
          "Content-Type": "application/json",
          ...(cookie ? { Cookie: cookie } : {}),
          ...extra,
        },
      },
      (res) => {
        let text = "";
        res.on("data", (c) => (text += c));
        res.on("end", () =>
          resolve({
            status: res.statusCode,
            headers: {
              get: (name) => {
                const v = res.headers[name];
                return Array.isArray(v) ? v.join(", ") : (v ?? null);
              },
            },
            json: async () => JSON.parse(text),
            text: async () => text,
          }),
        );
      },
    );
    r.on("error", reject);
    r.end(body === undefined ? undefined : JSON.stringify(body));
  });
}
test("external login never bootstraps admin, protects evidence and shares the same data", async (t) => {
  const a = await setup(t);
  let r = await remote(a, "/api/bootstrap", {});
  assert.equal((await r.json()).loginRequired, true);
  assert.equal(r.headers.get("set-cookie"), null);
  for (const path of [
    "/api/state",
    "/api/evidence/00000000-0000-4000-8000-000000000000",
    "/api/installer",
    "/api/events",
  ])
    assert.equal((await remote(a, path)).status, 401);
  assert.equal(
    (
      await remote(
        a,
        "/api/state",
        undefined,
        `rmslink_session=${a.store.adminToken}`,
      )
    ).status,
    401,
  );
  assert.equal(
    (await remote(a, "/api/login", { password: "wrong" })).status,
    401,
  );
  r = await remote(a, "/api/login", {
    password: a.store.secret("dashboard-access.key"),
  });
  assert.equal(r.status, 200);
  const cookie = r.headers.get("set-cookie");
  assert.match(cookie, /Secure/);
  assert.match(cookie, /HttpOnly/);
  assert.match(cookie, /SameSite=Strict/);
  const session = cookie.split(";")[0];
  const external = await (
    await remote(a, "/api/state", undefined, session)
  ).json();
  const local = await (
    await fetch(a.url + "/api/state", {
      headers: { Authorization: `Bearer ${a.store.adminToken}` },
    })
  ).json();
  assert.deepEqual(external.devices, local.devices);
  assert.deepEqual(external.rooms, local.rooms);
  assert.equal(external.access.remote, true);
  assert.equal(
    (await remote(a, "/api/access", undefined, session)).status,
    403,
  );
  assert.equal(
    (
      await remote(
        a,
        "/api/labels",
        { hotelId: "9", line: "101 문열림", room: "101", code: "DOOR_OPEN" },
        session,
        { Origin: "https://evil.test" },
      )
    ).status,
    403,
  );
  assert.equal(
    (await remote(a, "/api/analyze", {}, session, { "X-Rmslink-Client": "" }))
      .status,
    403,
  );
  const shortcut = await (
    await remote(a, "/api/shortcut", undefined, session)
  ).text();
  assert.match(shortcut, /URL=https:\/\/rms.example.test\//);
  assert(!shortcut.includes(a.store.adminToken));
  await remote(a, "/api/logout", {}, session);
  assert.equal((await remote(a, "/api/state", undefined, session)).status, 401);
});
test("expired sessions and repeated wrong passwords are rejected", () => {
  const auth = new DashboardAuth({ secret: () => "valid" });
  const r = auth.login("valid");
  assert(auth.valid(r.token));
  auth.sessions.set(r.token, Date.now() - 1);
  assert(!auth.valid(r.token));
  for (let i = 0; i < 12; i++) assert.equal(auth.login("bad").status, 401);
  assert.equal(auth.login("valid").status, 429);
});
test("Vercel origin uses remote authentication, current shared state and dashboard shortcuts", async (t) => {
  const dashboardOrigin = "https://rms-link.vercel.app";
  const upstreamOrigin = "https://rms.example.test:8443";
  const a = await setup(t, { dashboardOrigin, publicOrigin: upstreamOrigin });
  const headers = { Origin: dashboardOrigin };
  const call = (path, body, cookie, extra = {}) =>
    remote(a, path, body, cookie, { ...headers, ...extra });
  const bootstrap = await call("/api/bootstrap", {});
  assert.equal(bootstrap.status, 200);
  assert.deepEqual(await bootstrap.json(), {
    ok: false,
    loginRequired: true,
    remote: true,
  });
  assert.equal(bootstrap.headers.get("set-cookie"), null);
  assert.equal(
    (
      await call(
        "/api/state",
        undefined,
        `rmslink_session=${a.store.adminToken}`,
      )
    ).status,
    401,
  );
  const login = await call("/api/login", {
    password: a.store.secret("dashboard-access.key"),
  });
  assert.equal(login.status, 200);
  const cookie = login.headers.get("set-cookie");
  assert.match(cookie, /Secure/);
  assert(!/Domain=/i.test(cookie));
  const session = cookie.split(";")[0];
  const response = await call("/api/state", undefined, session);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("vercel-cdn-cache-control"), "no-store");
  const snapshot = await response.json();
  assert.equal(snapshot.access.publicOrigin, dashboardOrigin);
  assert.deepEqual(snapshot.rooms, a.store.snapshot().rooms);
  assert.equal((await call("/api/access", undefined, session)).status, 403);
  assert.equal(
    (
      await call("/api/state", undefined, session, {
        Origin: "https://evil.vercel.app",
        "X-Forwarded-Host": "rms-link.vercel.app",
      })
    ).status,
    403,
  );
  assert.equal(
    (await call("/api/state", undefined, session, { Host: "evil.test" }))
      .status,
    403,
  );
  const localBootstrap = await fetch(a.url + "/api/bootstrap", {
    method: "POST",
    headers: { Origin: dashboardOrigin, "X-Rmslink-Client": "1" },
  });
  assert.equal(localBootstrap.status, 403);
  const shortcut = await (
    await call("/api/shortcut", undefined, session)
  ).text();
  assert.equal(shortcut, `[InternetShortcut]\r\nURL=${dashboardOrigin}/\r\n`);
  writeFileSync(join(a.store.dir, "installer.exe"), "test installer");
  const download = await (
    await call("/api/installer-link", undefined, session)
  ).json();
  assert(download.url.startsWith(`${upstreamOrigin}/download/`));
  const installer = await call("/api/installer", undefined, session);
  assert.equal(installer.status, 302);
  assert.equal(installer.headers.get("location"), download.url);
  await call("/api/logout", {}, session);
  assert.equal((await call("/api/state", undefined, session)).status, 401);
});
test("live capture schema accepts explicit app and null while preserving error diagnostics", async (t) => {
  const { observationSchema } = await import("../control/domain.mjs");
  const base = {
    capturedAt: new Date().toISOString(),
    source: "none",
    ocrLanguage: "ko",
    errors: ["APP_NOT_SELECTED: 선택 필요"],
    lines: [],
    selectedApp: null,
    captureMethod: "none",
  };
  assert.equal(observationSchema.parse(base).selectedApp, null);
  assert.equal(
    observationSchema.parse({
      ...base,
      selectedApp: {
        name: "키텍 앱",
        process: "VendorRooms",
        title: "객실현황",
        method: "explicit-selection",
      },
    }).selectedApp.process,
    "VendorRooms",
  );
});

test("candidate installer requires download capability and does not replace published installer", async (t) => {
  const a = await setup(t);
  const { mkdirSync } = await import("node:fs");
  const candidate = "a".repeat(64);
  mkdirSync(join(a.store.dir, "install-candidates"));
  writeFileSync(join(a.store.dir, "installer.exe"), "published");
  writeFileSync(
    join(a.store.dir, "install-candidates", candidate + ".exe"),
    "candidate",
  );
  const path = "/download/" + a.store.downloadToken + "/RmsLink-Setup.exe";
  assert.equal(await (await fetch(a.agentUrl + path)).text(), "published");
  assert.equal(
    await (await fetch(a.agentUrl + path + "?candidate=" + candidate)).text(),
    "candidate",
  );
  assert.equal(
    (await fetch(a.agentUrl + path + "?candidate=../installer")).status,
    400,
  );
  assert.equal(
    (
      await fetch(
        a.agentUrl + "/download/wrong/RmsLink-Setup.exe?candidate=" + candidate,
      )
    ).status,
    401,
  );
});
