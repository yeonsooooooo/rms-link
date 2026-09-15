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
    pollMs: z.number().int().min(1000).max(30000),
    ocrScale: z.number().int().min(1).max(5),
  })
  .strict()
  .refine(
    (p) =>
      Object.keys(p.aliases).length <= 100 &&
      Object.keys(p.roomMap).length <= 500,
  );
export const commonProfile = {
  revision: 1,
  hotelId: "*",
  name: "공통 키텍 이벤트",
  mode: "events",
  roomPattern: "(?<![\\d:])(\\d{3,4})\\s*호?(?![\\d:])",
  aliases: {},
  roomMap: {},
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
    source: z.enum(["none", "ocr", "uia"]),
    ocrLanguage: z.string().max(50),
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
      "맥 미니가 객실·시각·이벤트 어휘를 분석합니다.",
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
    const fresh =
      device &&
      device.captureFresh !== false &&
      device.status !== "attention" &&
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
