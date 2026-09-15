import test from "node:test";
import { request } from "node:http";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createApp } from "../control/app.mjs";
import { DashboardAuth } from "../control/auth.mjs";
const publicOrigin = "https://rms.example.test";
async function setup(t) {
  const directory = mkdtempSync(join(tmpdir(), "rms-access-"));
  const app = await createApp({
    directory,
    publicOrigin,
    adminPort: 0,
    agentPort: 0,
    autoAnalyze: false,
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
