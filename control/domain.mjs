import { z } from "zod";
import { createHash } from "node:crypto";
export const id = z.string().regex(/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,49}$/);
export const codes = [
  "DOOR_OPEN",
  "DOOR_CLOSE",
  "KEY_IN",
  "KEY_OUT",
  "KEY_IN_GUEST",
  "KEY_OUT_GUEST",
  "KEY_IN_CLEAN",
  "KEY_OUT_CLEAN",
];
const date = z.iso.datetime({ offset: true });
export const profileSchema = z
  .object({
    revision: z.number().int().positive(),
    hotelId: z.union([id, z.literal("*")]),
    name: z.string().min(1).max(100),
    mode: z.enum(["events", "snapshot"]),
    roomPattern: z.string().min(1).max(200),
    aliases: z.record(z.string().min(2).max(40), z.enum(codes)),
    roomMap: z.record(z.string().max(30), z.string().min(1).max(30)),
    expectedRooms: z
      .array(z.string().regex(/^[\p{L}\d_-]{1,30}$/u))
      .max(500)
      .default([]),
    liveClockPattern: z.string().max(200).default(""),
    pollMs: z.number().int().min(1000).max(30000),
    ocrScale: z.number().int().min(1).max(5),
  })
  .strict()
  .refine(
    (p) =>
      Object.keys(p.aliases).length <= 100 &&
      Object.keys(p.roomMap).length <= 500 &&
      new Set(p.expectedRooms).size === p.expectedRooms.length,
  );
export const commonProfile = {
  revision: 1,
  hotelId: "*",
  name: "공통 키텍 이벤트",
  mode: "events",
  roomPattern: "(?<![\\p{L}\\d:])(\\d{3,4})\\s*호?(?![\\d:])",
  aliases: {},
  roomMap: {},
  expectedRooms: [],
  liveClockPattern: "",
  pollMs: 1500,
  ocrScale: 3,
};
export const eventSchema = z
  .object({
    room: z.string().regex(/^[\p{L}\d_-]{1,30}$/u),
    code: z.enum(codes),
    kind: z.enum(["event", "snapshot"]),
    rawLine: z.string().max(1000),
    occurredAt: date,
    observedAt: date,
  })
  .strict();
const line = z.object({
  text: z.string().max(1000),
  code: z.string().nullable().optional(),
  room: z.string().nullable().optional(),
  reason: z.string().max(500).optional(),
});
export const observationSchema = z
  .object({
    capturedAt: date,
    source: z.enum(["none", "ocr", "uia", "hybrid"]),
    readerVersion: z.string().max(40).optional(),
    application: z
      .object({
        identity: z.string().regex(/^[a-f0-9]{64}$/),
        vendor: z.string().max(100),
        product: z.string().max(500),
        version: z.string().max(100),
      })
      .nullable()
      .optional(),
    warnings: z.array(z.string().max(1000)).max(30).optional(),
    uncertainFields: z
      .array(
        z.object({ room: z.string().max(30), field: z.enum(["door", "key"]) }),
      )
      .max(1000)
      .optional(),
    coverage: z
      .object({
        mode: z.enum(["events", "snapshot"]),
        suggestedMode: z.enum(["events", "snapshot"]),
        expectedRooms: z.array(z.string().max(30)).max(500),
        observedRooms: z.array(z.string().max(30)).max(500),
        pending: z.number().int().min(0).max(600),
        snapshotEvidenceAt: date.nullable(),
      })
      .optional(),
    readings: z.array(eventSchema).max(600).optional(),
    ocrLanguage: z.string().max(50),
    selectedApp: z
      .object({
        name: z.string().max(160),
        process: z.string().max(100),
        title: z.string().max(160),
        method: z.string().max(40),
      })
      .nullable()
      .optional(),
    captureMethod: z.string().max(40).optional(),
    os: z.string().max(100).optional(),
    desktopAvailable: z.boolean().optional(),
    remoteSession: z.boolean().optional(),
    windows: z
      .array(
        z.object({
          handle: z.number(),
          process: z.string().max(100),
          title: z.string().max(160),
          x: z.number(),
          y: z.number(),
          w: z.number(),
          h: z.number(),
          minimized: z.boolean(),
        }),
      )
      .max(10)
      .optional(),
    regions: z
      .array(
        z.object({
          x: z.number(),
          y: z.number(),
          w: z.number(),
          h: z.number(),
        }),
      )
      .max(4)
      .optional(),
    errors: z.array(z.string().max(1000)).max(30),
    lines: z.array(line).max(100),
    unmatched: z
      .array(
        z.object({ text: z.string().max(300), reason: z.string().max(500) }),
      )
      .max(40)
      .optional(),
    image: z.string().max(540000).nullable().optional(),
  })
  .strict();
export const batchSchema = z
  .object({
    id: z.uuid(),
    deviceId: z.uuid(),
    sessionId: z.uuid(),
    hotelId: id,
    version: z.string().regex(/^\d+\.\d+\.\d+$/),
    profileRevision: z.number().int().positive(),
    sentAt: date,
    observation: observationSchema,
    events: z.array(eventSchema).max(600),
  })
  .strict();
export const digest = (s) => createHash("sha256").update(s).digest("hex");
export const eventKey = (device, e) =>
  digest([device, e.room, e.code, e.kind, e.occurredAt].join("|"));
export function diagnose(observation) {
  const errors = observation?.errors ?? [];
  const messages = {
    READING_CONFLICT: [
      "판독 결과가 일치하지 않음",
      "화면 원문과 실제 객실·문·키 상태를 대조해 주세요.",
    ],
    ROOM_NOT_ALLOWED: [
      "등록되지 않은 객실 번호",
      "실제 객실 목록과 OCR 번호를 대조하고 화면 설정을 확인하세요.",
    ],
    LAYOUT_AMBIGUOUS: [
      "표의 행 또는 상태가 모호함",
      "한 객실의 번호와 상태가 함께 읽히도록 로그 영역을 좁혀 주세요.",
    ],
    APP_NOT_SELECTED: [
      "키텍 앱 선택 필요",
      "Windows의 RmsLink 연결 설정에서 키텍 아이콘을 선택해 주세요.",
    ],
    WINDOW_OCCLUDED: [
      "키텍 화면이 다른 창에 가려짐",
      "키텍 창을 앞으로 가져온 뒤 다시 확인하세요.",
    ],
    WINDOW_CAPTURE_TIMEOUT: [
      "키텍 화면 응답 지연",
      "앱이 응답하는지 확인하고 필요한 경우 RmsLink를 다시 시작하세요.",
    ],
    WINDOW_CAPTURE_EMPTY: [
      "키텍 화면 캡처가 비어 있음",
      "키텍 창을 앞으로 가져오세요. GPU/원격 화면은 현장 확인이 필요합니다.",
    ],
    DESKTOP_LOCKED: [
      "Windows 화면 잠김",
      "Windows에 로그인하고 RMS 창을 표시해 주세요.",
    ],
    WINDOW_NOT_FOUND: [
      "RMS 창을 찾지 못함",
      "키텍/RMS를 실행하거나 앱에서 로그 영역을 지정해 주세요.",
    ],
    WINDOW_AMBIGUOUS: [
      "RMS 창이 여러 개",
      "앱에서 키텍 로그 영역을 직접 지정해 주세요.",
    ],
    WINDOW_MINIMIZED: ["RMS 창 최소화", "키텍/RMS 창을 복원해 주세요."],
    OCR_MISSING: [
      "OCR 엔진 없음",
      "Windows 한국어 OCR 언어팩을 설치해 주세요.",
    ],
    OCR_KOREAN_MISSING: [
      "한국어 OCR 없음",
      "한국어 OCR 언어팩을 설치해 주세요.",
    ],
    NO_TEXT: [
      "읽을 수 있는 글자가 없음",
      "다른 창에 가렸는지, 캡처 영역과 글자 크기를 확인해 주세요.",
    ],
    PARSE_NO_MATCH: [
      "호텔 화면 형식 분석 필요",
      "분석 서버가 객실·시각·이벤트 어휘를 분석합니다.",
    ],
    REGION_INVALID: [
      "캡처 영역이 화면 밖에 있음",
      "변경된 해상도에 맞춰 로그 영역을 다시 지정해 주세요.",
    ],
    CAPTURE_FAILURE: [
      "화면 수집 실패",
      "수집 오류와 Windows 화면 상태를 확인합니다.",
    ],
  };
  return errors.map((raw) => {
    const code = raw.split(":")[0];
    const [title, action] = messages[code] ?? [
      code,
      "진단 상세를 확인해 주세요.",
    ];
    return { code, title, action, detail: raw };
  });
}
export function stateForRoom(room, devices, now = Date.now()) {
  function field(name) {
    const data = room[name];
    if (!data)
      return { value: null, quality: "unknown", reason: "아직 관측되지 않음" };
    const device = devices.find((d) => d.id === data.deviceId);
    const uncertain = device?.batch?.observation?.uncertainFields?.some(
      (x) => x.room === room.room && x.field === name,
    );
    const fresh =
      device &&
      device.captureFresh !== false &&
      device.online !== false &&
      device.revoked !== 1 &&
      device.status !== "attention" &&
      device.verification?.profileCurrent !== false &&
      !uncertain &&
      now - Date.parse(device.last_seen) < 45000 &&
      now - Date.parse(data.at) < (data.kind === "snapshot" ? 60000 : 300000) &&
      device.session_id === data.sessionId;
    return {
      ...data,
      value: fresh ? data.value : null,
      lastValue: data.value,
      quality: fresh
        ? data.kind === "snapshot"
          ? "observed"
          : "inferred"
        : "stale",
      reason: fresh
        ? data.kind === "snapshot"
          ? "현재 화면 관측"
          : "마지막 이벤트에서 추정"
        : uncertain
          ? "현재 화면에서 상태 확인 필요"
          : "연결 또는 상태 근거가 오래됨",
    };
  }
  return { ...room, door: field("door"), key: field("key") };
}
export function applyEvent(room, e, deviceId, sessionId) {
  const field = e.code.startsWith("DOOR") ? "door" : "key";
  if (room[field] && Date.parse(room[field].at) > Date.parse(e.occurredAt))
    return room;
  const value =
    field === "door"
      ? e.code === "DOOR_OPEN"
        ? "open"
        : "closed"
      : e.code.startsWith("KEY_IN")
        ? "inserted"
        : "removed";
  // Contradictory reports at the same source time remain unknown.
  if (
    room[field] &&
    Date.parse(room[field].at) === Date.parse(e.occurredAt) &&
    room[field].value !== value
  ) {
    room[field] = { ...room[field], value: null, conflict: true };
    return room;
  }
  if (
    room[field]?.conflict &&
    Date.parse(room[field].at) === Date.parse(e.occurredAt)
  )
    return room;
  return {
    ...room,
    [field]: {
      value,
      at: e.occurredAt,
      kind: e.kind,
      deviceId,
      sessionId,
      code: e.code,
    },
  };
}
