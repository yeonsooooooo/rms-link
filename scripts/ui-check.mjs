import { chromium } from "playwright";
import { createApp } from "../control/app.mjs";
import { commonProfile } from "../control/domain.mjs";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { randomUUID } from "node:crypto";
import assert from "node:assert/strict";
const dir = mkdtempSync(join(tmpdir(), "rms-ui-")),
  artifacts = resolve("artifacts");
mkdirSync(artifacts, { recursive: true });
const app = await createApp({
  directory: dir,
  adminPort: 0,
  agentPort: 0,
  autoAnalyze: false,
});
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
try {
  await page.goto(app.url);
  await page.getByText("첫 호텔의 연결을 기다리고 있습니다").waitFor();
  await page.screenshot({
    path: join(artifacts, "dashboard-empty.png"),
    fullPage: true,
  });
  const deviceId = randomUUID(),
    sessionId = randomUUID(),
    now = new Date().toISOString();
  app.store.run(
    "INSERT INTO devices(id,token_hash,machine,hotel_id,session_id,last_seen,version,profile_revision,update_status,created_at) VALUES(?,?,?,?,?,?,?,?,?,?)",
    deviceId,
    "test",
    "테스트 PC · 현장 시뮬레이션",
    "9",
    sessionId,
    now,
    "0.2.0",
    2,
    "최신 버전",
    now,
  );
  app.store.run("INSERT INTO sessions VALUES(?,?,?)", sessionId, deviceId, "9");
  app.store.run(
    "INSERT INTO profiles VALUES(?,?,?,?,?,?)",
    "9",
    2,
    JSON.stringify({
      ...commonProfile,
      hotelId: "9",
      revision: 2,
      mode: "snapshot",
      expectedRooms: ["101", "102", "103"],
    }),
    "active",
    now,
    "test",
  );
  const readings = [
    {
      room: "101",
      code: "DOOR_CLOSE",
      kind: "snapshot",
      rawLine: "101 문닫힘",
      occurredAt: now,
      observedAt: now,
    },
    {
      room: "101",
      code: "KEY_IN_GUEST",
      kind: "snapshot",
      rawLine: "101 고객키삽입",
      occurredAt: now,
      observedAt: now,
    },
  ];
  app.store.ingest({
    id: randomUUID(),
    deviceId,
    sessionId,
    hotelId: "9",
    profileRevision: 2,
    observation: {
      capturedAt: now,
      source: "hybrid",
      readerVersion: "0.4.0",
      application: {
        identity: "a".repeat(64),
        vendor: "가람",
        product: "테스트 RMS",
        version: "fixture 1",
      },
      warnings: [
        "ROOMS_NOT_VISIBLE: 이번 화면에 없는 객실은 미확인으로 표시됩니다",
      ],
      coverage: {
        mode: "snapshot",
        suggestedMode: "snapshot",
        expectedRooms: ["101", "102", "103"],
        observedRooms: ["101", "102"],
        pending: 0,
        snapshotEvidenceAt: now,
      },
      readings: readings.map((r) => ({ ...r, source: "hybrid" })),
      channelReadings: [
        { ...readings[0], source: "uia" },
        { ...readings[0], source: "ocr", room: "102" },
      ],
      methods: {
        uia: {
          status: "ok",
          lineCount: 2,
          candidateCount: 2,
          acceptedCount: 2,
        },
        ocr: {
          status: "ok",
          lineCount: 2,
          candidateCount: 2,
          acceptedCount: 2,
        },
      },
      ocrLanguage: "ko",
      os: "Windows 11 (test fixture)",
      errors: [],
      lines: [
        { text: "101 고객키삽입 20:32:10", code: "KEY_IN_GUEST", room: "101" },
      ],
    },
    events: [
      {
        room: "101",
        code: "KEY_IN_GUEST",
        kind: "snapshot",
        rawLine: "101 고객키삽입",
        occurredAt: now,
        observedAt: now,
      },
      {
        room: "101",
        code: "DOOR_CLOSE",
        kind: "snapshot",
        rawLine: "101 문닫힘",
        occurredAt: now,
        observedAt: now,
      },
      {
        room: "102",
        code: "DOOR_OPEN",
        kind: "event",
        rawLine: "102 문열림",
        occurredAt: now,
        observedAt: now,
      },
    ],
  });
  await page.getByRole("button", { name: "↻ 새로고침" }).click();
  await page
    .getByText("테스트 PC · 현장 시뮬레이션", { exact: true })
    .waitFor();
  await page.screenshot({
    path: join(artifacts, "dashboard-test-data.png"),
    fullPage: true,
  });
  await page.getByText("테스트 PC · 현장 시뮬레이션", { exact: true }).click();
  await page.getByRole("heading", { name: "판독 원문과 실패 사유" }).waitFor();
  await page.getByText("이번 화면에서 확인되지 않은 객실: 103").waitFor();
  await page.getByRole("heading", { name: "어떻게 읽었나요?" }).waitFor();
  await page
    .getByRole("button", { name: "방법별 결과 맞음·틀림 기록" })
    .click();
  await page.locator("#reading-index").selectOption("1");
  await page.locator("#reading-room").fill("101");
  await page.locator("#reading-code").selectOption("DOOR_CLOSE");
  await page.locator("#reading-confirm").check();
  await page.locator("[data-save]").click();
  await page.locator(".modal-backdrop").waitFor({ state: "detached" });
  assert.equal(app.store.get("SELECT matched FROM reading_checks").matched, 0);
  await page.getByRole("button", { name: "실제 문·키 동작 확인" }).click();
  await page.locator("#field-confirm").check();
  await page.locator("[data-save]").click();
  await page.locator(".modal-backdrop").waitFor({ state: "detached" });
  assert.equal(app.store.get("SELECT COUNT(*) AS n FROM field_checks").n, 1);
  await page.getByRole("button", { name: "화면·객실 설정" }).click();
  assert.equal(await page.locator("#screen-mode").inputValue(), "snapshot");
  assert.equal(
    await page.locator("#screen-rooms").inputValue(),
    "101, 102, 103",
  );
  await page.locator("[data-close]").click();
  await page.screenshot({
    path: join(artifacts, "screen-verification-041.png"),
    fullPage: true,
  });
  await page.getByRole("button", { name: "호텔별 규칙 편집" }).click();
  const profile = JSON.parse(await page.locator("#profile-json").inputValue());
  assert.equal(profile.hotelId, "9");
  await page.locator("[data-close]").click();
  await page.getByRole("button", { name: "확인한 판독 샘플 등록" }).click();
  await page.locator("#label-line").fill("101 고객키투입 12:00:00");
  await page.locator("#label-room").fill("101");
  await page.locator("#label-code").selectOption("KEY_IN_GUEST");
  await page.locator("[data-save]").click();
  assert.equal(app.store.get("SELECT COUNT(*) AS n FROM labels").n, 1);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: join(artifacts, "dashboard-mobile.png"),
    fullPage: true,
  });
  assert(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  );
  assert.deepEqual(errors, []);
  writeFileSync(
    join(artifacts, "ui-result.json"),
    JSON.stringify(
      {
        passed: true,
        pageErrors: errors,
        mobileOverflow: false,
        fixtureOnly: true,
      },
      null,
      2,
    ),
  );
  console.log(
    "UI checks passed: empty, populated, details, profile editor, sample save, mobile overflow",
  );
} finally {
  await browser.close();
  await app.close();
  rmSync(dir, { recursive: true, force: true });
}
