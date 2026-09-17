import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { randomUUID, verify } from "node:crypto";
import { createApp } from "../control/app.mjs";
import { applyEvent, stateForRoom, commonProfile } from "../control/domain.mjs";
const now = () => new Date().toISOString();
async function setup(t) {
  const dir = mkdtempSync(join(tmpdir(), "rmslink-test-"));
  const app = await createApp({
    directory: dir,
    adminPort: 0,
    agentPort: 0,
    autoAnalyze: false,
    corePath: process.env.RMSLINK_CORE,
  });
  t.after(async () => {
    await app.close();
    rmSync(dir, { recursive: true, force: true });
  });
  return app;
}
async function req(url, path, body, token, method) {
  const r = await fetch(url + path, {
    method: method ?? (body === undefined ? "GET" : "POST"),
    headers: {
      "Content-Type": "application/json",
      "X-Rmslink-Client": "1",
      ...(token ? { Authorization: "Bearer " + token } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  let data;
  try {
    data = await r.json();
  } catch {}
  return { status: r.status, data, headers: r.headers };
}
async function enrolled(app) {
  const token = "a".repeat(64),
    deviceId = randomUUID(),
    sessionId = randomUUID();
  let r = await req(
    app.agentUrl,
    "/agent/enroll",
    { deviceId, enrollmentCode: app.store.enrollment(), machine: "test-pc" },
    token,
  );
  assert.equal(r.status, 201);
  await req(
    app.agentUrl,
    "/agent/heartbeat",
    {
      deviceId,
      sessionId,
      hotelId: "9",
      version: "0.2.0",
      profileRevision: 1,
      pending: 0,
      updateStatus: "최신",
    },
    token,
  );
  return { deviceId, sessionId, token };
}
function batch(d, extra = {}) {
  return {
    id: randomUUID(),
    deviceId: d.deviceId,
    sessionId: d.sessionId,
    hotelId: "9",
    version: "0.2.0",
    profileRevision: 1,
    sentAt: now(),
    observation: {
      capturedAt: now(),
      source: "ocr",
      ocrLanguage: "ko",
      errors: [],
      lines: [],
    },
    events: [
      {
        room: "101",
        code: "DOOR_OPEN",
        kind: "event",
        rawLine: "101 문열림",
        occurredAt: now(),
        observedAt: now(),
      },
    ],
    ...extra,
  };
}
test("untrusted host cannot access dashboard state or local bootstrap", async (t) => {
  const a = await setup(t);
  assert.equal((await req(a.agentUrl, "/api/state")).status, 403);
  assert.equal((await req(a.agentUrl, "/api/bootstrap", {})).status, 403);
  assert.equal((await req(a.url, "/api/state")).status, 401);
  assert.equal((await req(a.url, "/api/bootstrap", {})).status, 200);
  const foreign = await fetch(a.url + "/api/bootstrap", {
    method: "POST",
    headers: { Origin: "https://attacker.test", "X-Rmslink-Client": "1" },
  });
  assert.equal(foreign.status, 403);
});
test("enrollment requires a capability; cannot overwrite or revoke bypass device identity", async (t) => {
  const a = await setup(t);
  assert.equal(
    (
      await req(
        a.agentUrl,
        "/agent/enroll",
        { deviceId: randomUUID(), machine: "pc", enrollmentCode: "bad" },
        "a".repeat(64),
      )
    ).status,
    400,
  );
  const d = await enrolled(a);
  assert.equal(
    (
      await req(
        a.agentUrl,
        "/agent/enroll",
        {
          deviceId: d.deviceId,
          machine: "pc",
          enrollmentCode: a.store.enrollment(),
        },
        "b".repeat(64),
      )
    ).status,
    403,
  );
  a.store.run("UPDATE devices SET revoked=1 WHERE id=?", d.deviceId);
  assert.equal(
    (await req(a.agentUrl, "/agent/updates?hotelId=9", undefined, d.token))
      .status,
    401,
  );
});
test("durable batches are idempotent across retries and restart; timestamps never regress room state", async (t) => {
  const a = await setup(t),
    d = await enrolled(a),
    b = batch(d);
  for (let i = 0; i < 2; i++)
    assert.equal(
      (await req(a.agentUrl, "/agent/observations", b, d.token)).data.ack,
      b.id,
    );
  assert.equal(a.store.get("SELECT count(*) AS n FROM batches").n, 1);
  assert.equal(a.store.get("SELECT count(*) AS n FROM events").n, 1);
  const old = batch(d, {
    events: [
      {
        ...b.events[0],
        code: "DOOR_CLOSE",
        occurredAt: new Date(Date.now() - 60000).toISOString(),
      },
    ],
  });
  await req(a.agentUrl, "/agent/observations", old, d.token);
  assert.equal(a.store.snapshot("9").rooms[0].door.value, "open");
  assert.equal(a.store.snapshot("10").rooms.length, 0);
});
test("wrong device/session/hotel and future time rejected atomically", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  for (const changes of [
    { deviceId: randomUUID() },
    { hotelId: "10" },
    {
      events: [
        {
          ...batch(d).events[0],
          occurredAt: new Date(Date.now() + 3600000).toISOString(),
        },
      ],
    },
  ]) {
    const r = await req(
      a.agentUrl,
      "/agent/observations",
      batch(d, changes),
      d.token,
    );
    assert.equal(r.status, 400);
  }
  assert.equal(a.store.get("SELECT count(*) AS n FROM batches").n, 0);
});
test("old queues do not overwrite live heartbeat/current hotel and are visibly stale", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  const b = batch(d);
  b.events[0].occurredAt = new Date(Date.now() - 600000).toISOString();
  await req(a.agentUrl, "/agent/observations", b, d.token);
  assert.equal(a.store.snapshot().rooms[0].door.value, null);
  assert.equal(a.store.snapshot().rooms[0].door.quality, "stale");
  const sessionId = randomUUID();
  await req(
    a.agentUrl,
    "/agent/heartbeat",
    {
      deviceId: d.deviceId,
      sessionId,
      hotelId: "10",
      version: "0.2.0",
      profileRevision: 1,
      pending: 1,
      updateStatus: "최신",
    },
    d.token,
  );
  await req(a.agentUrl, "/agent/observations", batch(d), d.token);
  assert.equal(a.store.get("SELECT hotel_id FROM devices").hotel_id, "10");
  assert.equal(a.store.snapshot().rooms[0].door.value, null);
});
test("signed update is scoped to enrolled hotels and detects payload mutation", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  const r = await req(
    a.agentUrl,
    "/agent/updates?hotelId=9",
    undefined,
    d.token,
  );
  assert.equal(r.status, 200);
  assert(
    verify(
      "RSA-SHA256",
      Buffer.from(r.data.payload, "base64"),
      a.store.publicKey,
      Buffer.from(r.data.signature, "base64"),
    ),
  );
  assert(
    !verify(
      "RSA-SHA256",
      Buffer.from("{}"),
      a.store.publicKey,
      Buffer.from(r.data.signature, "base64"),
    ),
  );
  assert.equal(
    (await req(a.agentUrl, "/agent/updates?hotelId=10", undefined, d.token))
      .status,
    403,
  );
});
test("diagnostic errors generate a persistent bounded analysis queue", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  const b = batch(d, {
    events: [],
    observation: {
      capturedAt: now(),
      source: "ocr",
      ocrLanguage: "ko",
      lines: [{ text: "101 고객키투입 12:00:00", reason: "어휘 불일치" }],
      errors: ["PARSE_NO_MATCH: 어휘"],
    },
  });
  await req(a.agentUrl, "/agent/observations", b, d.token);
  assert.equal(a.worker.enqueue(d.deviceId, true).queued, true);
  assert.equal(a.worker.enqueue(d.deviceId, true).queued, false);
  assert.equal(a.store.snapshot().devices[0].status, "attention");
});
test("current snapshots and historical inferences have independent freshness; conflicts stay unknown", () => {
  const d = { id: "a", last_seen: now(), session_id: "s" };
  let room = { room: "101" };
  const e = { code: "KEY_IN", kind: "snapshot", occurredAt: now() };
  room = applyEvent(room, e, "a", "s");
  assert.equal(stateForRoom(room, [d]).key.quality, "observed");
  assert.equal(stateForRoom(room, [d], Date.now() + 61000).key.value, null);
  room = applyEvent(room, { ...e, code: "KEY_OUT" }, "a", "s");
  assert.equal(stateForRoom(room, [d]).key.value, null);
  room = applyEvent(room, e, "a", "s");
  assert.equal(stateForRoom(room, [d]).key.value, null);
});
test(
  "profile publish runs shared C# parser and hotel regression before activation",
  { skip: !process.env.RMSLINK_CORE },
  async (t) => {
    const a = await setup(t);
    const p = {
      ...commonProfile,
      hotelId: "9",
      revision: 2,
      aliases: { 고객키투입: "KEY_IN_GUEST" },
    };
    a.store.run(
      "INSERT INTO labels(hotel_id,line,room,code,created_at) VALUES(?,?,?,?,?)",
      "9",
      "101 고객키투입 12:01:00",
      "101",
      "KEY_IN_GUEST",
      now(),
    );
    await a.worker.publish(p, "test");
    assert.equal(a.store.profile("9").revision, 2);
    assert.equal(a.store.profile("10").revision, 1);
    await assert.rejects(
      () =>
        a.worker.publish(
          { ...p, revision: 3, aliases: { 고객키투입: "KEY_OUT" } },
          "test",
        ),
      /샘플/,
    );
    assert.equal(a.store.profile("9").revision, 2);
  },
);

test("hybrid screen diagnostics preserve uncertainty and show unobserved configured rooms", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  a.store.run(
    "INSERT INTO profiles VALUES(?,?,?,?,?,?)",
    "9",
    2,
    JSON.stringify({
      ...commonProfile,
      hotelId: "9",
      revision: 2,
      expectedRooms: ["101", "102"],
    }),
    "active",
    now(),
    "test",
  );
  const b = batch(d, { profileRevision: 2 });
  b.observation = {
    ...b.observation,
    source: "hybrid",
    warnings: ["OCR_CONFIRMING: 확인 중"],
    uncertainFields: [{ room: "101", field: "door" }],
    coverage: {
      mode: "events",
      suggestedMode: "events",
      expectedRooms: ["101", "102"],
      observedRooms: ["101"],
      pending: 1,
      snapshotEvidenceAt: null,
    },
  };
  assert.equal(
    (await req(a.agentUrl, "/agent/observations", b, d.token)).status,
    200,
  );
  const s = a.store.snapshot("9");
  assert.equal(s.rooms.length, 2);
  assert.equal(s.rooms.find((r) => r.room === "101").door.value, null);
  assert.match(s.rooms.find((r) => r.room === "101").door.reason, /확인 필요/);
  assert.equal(s.rooms.find((r) => r.room === "102").key.quality, "unknown");
  assert.equal(s.devices[0].verification.status, "pending");
});

function fieldBatch(d, code, extra = {}) {
  const b = batch(d, { events: [] });
  b.observation = {
    ...b.observation,
    readerVersion: "0.4.0",
    source: "hybrid",
    application: {
      identity: "a".repeat(64),
      vendor: "가람",
      product: "Test fixture",
      version: "1",
    },
    selectedApp: {
      name: "키텍 앱",
      process: "fixture",
      title: "Room table",
      method: "explicit-selection",
    },
    readings: [
      {
        room: "101",
        code,
        kind: "event",
        rawLine: "101 " + code,
        occurredAt: now(),
        observedAt: now(),
      },
    ],
    ...extra,
  };
  return b;
}

test("physical check requires a recent accepted reading and four actions on the same room", async (t) => {
  const a = await setup(t),
    d = await enrolled(a),
    token = a.store.adminToken;
  const code = "DOOR_OPEN",
    b = fieldBatch(d, code);
  await req(a.agentUrl, "/agent/observations", b, d.token);
  let r = await req(
    a.url,
    "/api/field-checks",
    {
      deviceId: d.deviceId,
      batchId: b.id,
      room: "101",
      code,
      confirmed: false,
    },
    token,
  );
  assert.equal(r.status, 400);
  r = await req(
    a.url,
    "/api/field-checks",
    { deviceId: d.deviceId, batchId: b.id, room: "102", code, confirmed: true },
    token,
  );
  assert.equal(r.status, 400);
  for (const c of [
    "DOOR_OPEN",
    "DOOR_CLOSE",
    "KEY_IN_GUEST",
    "KEY_OUT_GUEST",
  ]) {
    const evidence = fieldBatch(d, c);
    await req(a.agentUrl, "/agent/observations", evidence, d.token);
    const saved = await req(
      a.url,
      "/api/field-checks",
      {
        deviceId: d.deviceId,
        batchId: evidence.id,
        room: "101",
        code: c,
        confirmed: true,
      },
      token,
    );
    assert.equal(saved.status, 200, JSON.stringify(saved.data));
  }
  assert.equal(
    a.store.snapshot().devices[0].verification.status,
    "sample_verified",
  );
  assert.equal(a.store.snapshot().devices[0].verification.room, "101");
  const changed = fieldBatch(d, "DOOR_OPEN", {
    application: {
      identity: "b".repeat(64),
      vendor: "씨리얼",
      product: "Other fixture",
      version: "2",
    },
  });
  await req(a.agentUrl, "/agent/observations", changed, d.token);
  assert.equal(a.store.snapshot().devices[0].verification.status, "pending");
  assert.throws(
    () =>
      a.store.confirmFieldCheck({
        deviceId: d.deviceId,
        batchId: b.id,
        room: "101",
        code,
      }),
    /최근 판독/,
  );
});

test("field checks cannot certify stale, uncertain, failed or previous-profile observations", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  for (const extra of [
    { capturedAt: new Date(Date.now() - 180000).toISOString() },
    { uncertainFields: [{ room: "101", field: "door" }] },
    { errors: ["READING_CONFLICT: conflict"] },
    {
      readings: [
        {
          room: "101",
          code: "DOOR_OPEN",
          kind: "event",
          rawLine: "old",
          occurredAt: new Date(Date.now() - 180000).toISOString(),
          observedAt: now(),
        },
      ],
    },
  ]) {
    const b = fieldBatch(d, "DOOR_OPEN", extra);
    a.store.ingest(b);
    assert.throws(() =>
      a.store.confirmFieldCheck({
        deviceId: d.deviceId,
        batchId: b.id,
        room: "101",
        code: "DOOR_OPEN",
      }),
    );
  }
  const b = fieldBatch(d, "DOOR_OPEN");
  a.store.ingest(b);
  a.store.run(
    "INSERT INTO profiles VALUES(?,?,?,?,?,?)",
    "9",
    2,
    JSON.stringify({ ...commonProfile, hotelId: "9", revision: 2 }),
    "active",
    now(),
    "test",
  );
  assert.throws(() =>
    a.store.confirmFieldCheck({
      deviceId: d.deviceId,
      batchId: b.id,
      room: "101",
      code: "DOOR_OPEN",
    }),
  );
  assert.equal(
    a.store.snapshot().devices[0].verification.profileCurrent,
    false,
  );
});

test(
  "hotel room inventory validation retains common parser regressions",
  { skip: !process.env.RMSLINK_CORE },
  async (t) => {
    const a = await setup(t);
    await a.worker.publish(
      {
        ...commonProfile,
        hotelId: "9",
        revision: 2,
        expectedRooms: ["201", "202"],
      },
      "test",
    );
    assert.deepEqual(a.store.profile("9").expectedRooms, ["201", "202"]);
    await assert.rejects(() =>
      a.worker.publish(
        {
          ...commonProfile,
          hotelId: "9",
          revision: 3,
          expectedRooms: ["201", "201"],
        },
        "test",
      ),
    );
  },
);

test("method provenance survives ingestion and human sample accuracy counts wrong answers without duplicates", async (t) => {
  const a = await setup(t),
    d = await enrolled(a);
  const b = fieldBatch(d, "DOOR_OPEN");
  const reading = { ...b.observation.readings[0], source: "uia" };
  b.observation.readings = [reading];
  b.observation.channelReadings = [
    reading,
    { ...reading, source: "ocr", room: "102" },
  ];
  b.observation.methods = {
    uia: { status: "ok", lineCount: 1, candidateCount: 1, acceptedCount: 1 },
    ocr: { status: "ok", lineCount: 1, candidateCount: 1, acceptedCount: 0 },
  };
  b.observation.lines = [
    { text: reading.rawLine, source: "uia", room: "101", code: "DOOR_OPEN" },
  ];
  b.events = [reading];
  assert.equal(
    (await req(a.agentUrl, "/agent/observations", b, d.token)).status,
    200,
  );
  let state = a.store.snapshot();
  assert.equal(state.events[0].source, "uia");
  assert.equal(state.rooms[0].door.source, "uia");
  assert.equal(state.devices[0].batch.observation.lines[0].source, "uia");
  assert.equal(state.devices[0].readingAccuracy[0].percent, null);
  const check = {
    deviceId: d.deviceId,
    batchId: b.id,
    index: 0,
    expectedRoom: "101",
    expectedCode: "DOOR_OPEN",
    confirmed: true,
  };
  for (const index of [0, 0, 1])
    assert.equal(
      (
        await req(
          a.url,
          "/api/reading-checks",
          { ...check, index },
          a.store.adminToken,
        )
      ).status,
      200,
    );
  state = a.store.snapshot();
  assert.deepEqual(
    state.devices[0].readingAccuracy.map((c) => [
      c.source,
      c.total,
      c.matched,
      c.percent,
    ]),
    [
      ["uia", 1, 1, 100],
      ["ocr", 1, 0, 0],
    ],
  );
  assert.equal(
    state.devices[0].verification.status,
    "pending",
    "Accuracy reviews cannot certify physical commissioning",
  );
  assert.equal(
    (
      await req(
        a.url,
        "/api/reading-checks",
        { ...check, index: 25 },
        a.store.adminToken,
      )
    ).status,
    400,
  );
  const next = fieldBatch(d, "DOOR_OPEN", {
    channelReadings: b.observation.channelReadings,
    readerVersion: "0.4.1",
  });
  assert.equal(
    (await req(a.agentUrl, "/agent/observations", next, d.token)).status,
    200,
  );
  assert.equal(
    a.store.snapshot().devices[0].readingAccuracy[0].percent,
    null,
    "New reader has a separate denominator",
  );
  assert.equal(
    (await req(a.url, "/api/reading-checks", check, a.store.adminToken)).status,
    400,
    "Old scope cannot be reviewed as current",
  );
});
