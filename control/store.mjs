import { readingMethods } from "./reading-methods.mjs";
import { DatabaseSync } from "node:sqlite";
import {
  mkdirSync,
  readFileSync,
  writeFileSync,
  existsSync,
  chmodSync,
} from "node:fs";
import { join } from "node:path";
import {
  randomBytes,
  generateKeyPairSync,
  sign,
  timingSafeEqual,
} from "node:crypto";
import {
  digest,
  commonProfile,
  diagnose,
  eventKey,
  applyEvent,
  stateForRoom,
} from "./domain.mjs";
export function safeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string") return false;
  const x = Buffer.from(a),
    y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}
export class Store {
  constructor(dir) {
    this.dir = dir;
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    chmodSync(dir, 0o700);
    mkdirSync(join(dir, "packages"), { recursive: true });
    mkdirSync(join(dir, "evidence"), { recursive: true });
    mkdirSync(join(dir, "jobs"), { recursive: true });
    this.db = new DatabaseSync(join(dir, "state.db"));
    this.db
      .exec(`PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;
  CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,token_hash TEXT NOT NULL,machine TEXT,hotel_id TEXT,session_id TEXT,last_seen TEXT,version TEXT,profile_revision INTEGER DEFAULT 1,pending INTEGER DEFAULT 0,update_status TEXT,revoked INTEGER DEFAULT 0,created_at TEXT);
  CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,device_id TEXT NOT NULL,hotel_id TEXT NOT NULL);
  CREATE TABLE IF NOT EXISTS batches(id TEXT PRIMARY KEY,device_id TEXT NOT NULL,hotel_id TEXT NOT NULL,captured_at TEXT,received_at TEXT,observation TEXT NOT NULL,profile_revision INTEGER,session_id TEXT);
  CREATE INDEX IF NOT EXISTS batch_hotel ON batches(hotel_id,captured_at DESC);
  CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY,device_id TEXT,hotel_id TEXT,room TEXT,code TEXT,kind TEXT,occurred_at TEXT,observed_at TEXT,raw_line TEXT,received_at TEXT);
  CREATE INDEX IF NOT EXISTS event_hotel ON events(hotel_id,occurred_at DESC);
  CREATE TABLE IF NOT EXISTS rooms(hotel_id TEXT,room TEXT,state TEXT,PRIMARY KEY(hotel_id,room));
  CREATE TABLE IF NOT EXISTS profiles(hotel_id TEXT,revision INTEGER,body TEXT,status TEXT,created_at TEXT,reason TEXT,PRIMARY KEY(hotel_id,revision));
  CREATE TABLE IF NOT EXISTS releases(version TEXT PRIMARY KEY,sha256 TEXT,size INTEGER,status TEXT,created_at TEXT);
  CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,hotel_id TEXT,device_id TEXT,batch_id TEXT,status TEXT,signature TEXT,created_at TEXT,updated_at TEXT,result TEXT);
  CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT,at TEXT,type TEXT,hotel_id TEXT,detail TEXT);
  CREATE TABLE IF NOT EXISTS enrollments(hash TEXT PRIMARY KEY,expires_at TEXT,remaining INTEGER);
  CREATE TABLE IF NOT EXISTS labels(id INTEGER PRIMARY KEY AUTOINCREMENT,hotel_id TEXT,line TEXT,room TEXT,code TEXT,created_at TEXT);
  CREATE TABLE IF NOT EXISTS field_checks(id INTEGER PRIMARY KEY AUTOINCREMENT,device_id TEXT,hotel_id TEXT,scope TEXT,room TEXT,code TEXT,raw_line TEXT,batch_id TEXT,confirmed_at TEXT);
  CREATE INDEX IF NOT EXISTS field_check_scope ON field_checks(device_id,scope);
  CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT);`);
    this.db.exec(
      "CREATE INDEX IF NOT EXISTS batch_device_session ON batches(device_id,session_id,captured_at DESC)",
    );
    // Additive migrations preserve installations and old evidence.
    if (!this.all("PRAGMA table_info(events)").some((c) => c.name === "source"))
      this.db.exec(
        "ALTER TABLE events ADD COLUMN source TEXT NOT NULL DEFAULT 'unknown'",
      );
    this.db
      .exec(`CREATE TABLE IF NOT EXISTS reading_checks(sample_key TEXT PRIMARY KEY,device_id TEXT,scope TEXT,batch_id TEXT,source TEXT,raw_line TEXT,room TEXT,code TEXT,expected_room TEXT,expected_code TEXT,matched INTEGER,checked_at TEXT);
      CREATE INDEX IF NOT EXISTS reading_check_scope ON reading_checks(device_id,scope);`);
    this.adminToken = this.secret("admin.token");
    this.downloadToken = this.secret("download.token");
    const priv = join(dir, "update-private.pem"),
      pub = join(dir, "update-public.pem");
    if (!existsSync(priv)) {
      const k = generateKeyPairSync("rsa", {
        modulusLength: 3072,
        publicKeyEncoding: { type: "spki", format: "pem" },
        privateKeyEncoding: { type: "pkcs8", format: "pem" },
      });
      writeFileSync(priv, k.privateKey, { mode: 0o600 });
      writeFileSync(pub, k.publicKey, { mode: 0o600 });
    }
    this.privateKey = readFileSync(priv);
    this.publicKey = readFileSync(pub, "utf8");
  }
  secret(name) {
    const file = join(this.dir, name);
    if (!existsSync(file))
      writeFileSync(file, randomBytes(32).toString("hex"), { mode: 0o600 });
    return readFileSync(file, "utf8").trim();
  }
  run(sql, ...p) {
    return this.db.prepare(sql).run(...p);
  }
  get(sql, ...p) {
    return this.db.prepare(sql).get(...p);
  }
  all(sql, ...p) {
    return this.db.prepare(sql).all(...p);
  }
  transaction(fn) {
    this.db.exec("BEGIN IMMEDIATE");
    try {
      const r = fn();
      this.db.exec("COMMIT");
      return r;
    } catch (e) {
      this.db.exec("ROLLBACK");
      throw e;
    }
  }
  audit(type, hotel, detail) {
    this.run(
      "INSERT INTO audit(at,type,hotel_id,detail) VALUES(?,?,?,?)",
      new Date().toISOString(),
      type,
      hotel ?? "",
      JSON.stringify(detail),
    );
  }
  enrollment() {
    const code = randomBytes(24).toString("hex");
    this.run(
      "INSERT INTO enrollments VALUES(?,?,?)",
      digest(code),
      new Date(Date.now() + 7 * 86400000).toISOString(),
      100,
    );
    return code;
  }
  authenticate(token) {
    return typeof token === "string"
      ? this.get(
          "SELECT * FROM devices WHERE token_hash=? AND revoked=0",
          digest(token),
        )
      : undefined;
  }
  profile(hotel) {
    const r = this.get(
      "SELECT body FROM profiles WHERE hotel_id=? AND status='active' ORDER BY revision DESC LIMIT 1",
      hotel,
    );
    return r ? JSON.parse(r.body) : commonProfile;
  }
  signed(offer) {
    const payload = Buffer.from(
      JSON.stringify({
        ...offer,
        expiresAt: new Date(Date.now() + 3600000).toISOString(),
      }),
    );
    return {
      payload: payload.toString("base64"),
      signature: sign("RSA-SHA256", payload, this.privateKey).toString(
        "base64",
      ),
    };
  }
  ingest(b) {
    if (this.get("SELECT id FROM batches WHERE id=?", b.id)) return;
    const now = new Date().toISOString();
    this.transaction(() => {
      const s = this.get("SELECT * FROM sessions WHERE id=?", b.sessionId);
      if (s && (s.device_id !== b.deviceId || s.hotel_id !== b.hotelId))
        throw new Error("세션 호텔 불일치");
      this.run(
        "INSERT OR IGNORE INTO sessions VALUES(?,?,?)",
        b.sessionId,
        b.deviceId,
        b.hotelId,
      );
      this.run(
        "INSERT INTO batches VALUES(?,?,?,?,?,?,?,?)",
        b.id,
        b.deviceId,
        b.hotelId,
        b.observation.capturedAt,
        now,
        JSON.stringify(b.observation),
        b.profileRevision,
        b.sessionId,
      );
      for (const e of b.events) {
        if (
          Date.parse(e.occurredAt) > Date.parse(e.observedAt) + 120000 ||
          Date.parse(e.observedAt) > Date.now() + 120000 ||
          Date.parse(e.observedAt) - Date.parse(e.occurredAt) > 370 * 86400000
        )
          throw new Error("이벤트 시각 범위 오류");
        const inserted = this.run(
          "INSERT OR IGNORE INTO events(id,device_id,hotel_id,room,code,kind,occurred_at,observed_at,raw_line,received_at,source) VALUES(?,?,?,?,?,?,?,?,?,?,?)",
          eventKey(b.deviceId, e),
          b.deviceId,
          b.hotelId,
          e.room,
          e.code,
          e.kind,
          e.occurredAt,
          e.observedAt,
          e.rawLine,
          now,
          e.source ?? "unknown",
        );
        if (!inserted.changes) continue;
        const row = this.get(
          "SELECT state FROM rooms WHERE hotel_id=? AND room=?",
          b.hotelId,
          e.room,
        );
        const state = applyEvent(
          row ? JSON.parse(row.state) : { hotelId: b.hotelId, room: e.room },
          e,
          b.deviceId,
          b.sessionId,
        );
        this.run(
          "INSERT INTO rooms VALUES(?,?,?) ON CONFLICT(hotel_id,room) DO UPDATE SET state=excluded.state",
          b.hotelId,
          e.room,
          JSON.stringify(state),
        );
      }
    });
  }
  checkScope(batch) {
    const o =
      typeof batch?.observation === "string"
        ? JSON.parse(batch.observation)
        : batch?.observation;
    if (!o?.application?.identity || !o.readerVersion) return null;
    return digest(
      JSON.stringify([
        batch.hotel_id,
        batch.profile_revision,
        o.readerVersion,
        o.application.identity,
        o.selectedApp?.title,
        o.regions,
      ]),
    );
  }
  verification(device, batch) {
    const scope = this.checkScope(batch);
    const checks = scope
      ? this.all(
          "SELECT room,code,raw_line,confirmed_at FROM field_checks WHERE device_id=? AND scope=? ORDER BY id DESC",
          device.id,
          scope,
        )
      : [];
    const steps = ["DOOR_OPEN", "DOOR_CLOSE", "KEY_IN", "KEY_OUT"];
    const rooms = [...new Set(checks.map((c) => c.room))]
      .map((room) => {
        const matched = checks.filter(
          (c) => c.room === room && !c.code.endsWith("_CLEAN"),
        );
        const completed = steps.filter((step) =>
          matched.some((c) => c.code === step || c.code === step + "_GUEST"),
        );
        return { room, completed };
      })
      .sort((a, b) => b.completed.length - a.completed.length);
    const best = rooms[0] ?? { room: null, completed: [] };
    const profileCurrent =
      batch?.profile_revision === this.profile(device.hotel_id).revision;
    return {
      status:
        best.completed.length === 4 && profileCurrent
          ? "sample_verified"
          : "pending",
      ...best,
      required: steps,
      profileCurrent,
      checks: checks.slice(0, 12),
    };
  }
  confirmFieldCheck({ deviceId, batchId, room, code }) {
    const device = this.get(
      "SELECT * FROM devices WHERE id=? AND revoked=0",
      deviceId,
    );
    const batch = this.get(
      "SELECT * FROM batches WHERE id=? AND device_id=?",
      batchId,
      deviceId,
    );
    const latest =
      device &&
      this.get(
        "SELECT * FROM batches WHERE device_id=? AND session_id=? ORDER BY captured_at DESC LIMIT 1",
        deviceId,
        device.session_id,
      );
    const scope = this.checkScope(batch);
    const o = batch && JSON.parse(batch.observation);
    const live = latest && JSON.parse(latest.observation);
    if (
      !device ||
      !batch ||
      !scope ||
      batch.session_id !== device.session_id ||
      batch.hotel_id !== device.hotel_id ||
      scope !== this.checkScope(latest) ||
      batch.profile_revision !== this.profile(device.hotel_id).revision ||
      Date.now() - Date.parse(device.last_seen) > 45000 ||
      Date.now() - Date.parse(batch.captured_at) > 120000 ||
      Date.now() - Date.parse(latest.captured_at) > 30000 ||
      o.errors.length ||
      live.errors.length
    )
      throw new Error(
        "현재 연결·화면·규칙과 일치하는 최근 판독만 확인할 수 있습니다. 화면을 새로 확인하세요.",
      );
    const reading = o.readings?.find(
      (e) =>
        e.room === room &&
        e.code === code &&
        Date.now() - Date.parse(e.occurredAt) < 120000,
    );
    if (
      !reading ||
      o.uncertainFields?.some(
        (f) =>
          f.room === room &&
          f.field === (code.startsWith("DOOR") ? "door" : "key"),
      )
    )
      throw new Error(
        "최근 화면에서 확인된 객실·상태가 아닙니다. 실제 동작 후 새 판독을 기다려 주세요.",
      );
    this.transaction(() => {
      this.run(
        "INSERT INTO field_checks(device_id,hotel_id,scope,room,code,raw_line,batch_id,confirmed_at) VALUES(?,?,?,?,?,?,?,?)",
        deviceId,
        device.hotel_id,
        scope,
        room,
        code,
        reading.rawLine,
        batchId,
        new Date().toISOString(),
      );
      this.audit("field_check_confirmed", device.hotel_id, {
        deviceId,
        room,
        code,
        batchId,
      });
    });
    return this.verification(device, latest);
  }
  readingAccuracy(deviceId, batch) {
    const scope = this.checkScope(batch);
    const counts = scope
      ? this.all(
          "SELECT source,COUNT(*) AS total,SUM(matched) AS matched FROM reading_checks WHERE device_id=? AND scope=? GROUP BY source",
          deviceId,
          scope,
        )
      : [];
    return ["uia", "ocr"].map((source) => {
      const c = counts.find((c) => c.source === source) ?? {
        total: 0,
        matched: 0,
      };
      return {
        source,
        total: c.total,
        matched: c.matched,
        mismatched: c.total - c.matched,
        percent: c.total ? Math.round((c.matched / c.total) * 1000) / 10 : null,
      };
    });
  }
  checkReading({ deviceId, batchId, index, expectedRoom, expectedCode }) {
    const device = this.get(
      "SELECT * FROM devices WHERE id=? AND revoked=0",
      deviceId,
    );
    const batch = this.get(
      "SELECT * FROM batches WHERE id=? AND device_id=?",
      batchId,
      deviceId,
    );
    const scope = this.checkScope(batch);
    const latest =
      device &&
      this.get(
        "SELECT * FROM batches WHERE device_id=? AND session_id=? ORDER BY captured_at DESC LIMIT 1",
        deviceId,
        device.session_id,
      );
    if (
      !device ||
      !scope ||
      batch.session_id !== device.session_id ||
      batch.hotel_id !== device.hotel_id ||
      scope !== this.checkScope(latest) ||
      batch.profile_revision !== this.profile(device.hotel_id).revision ||
      Date.now() - Date.parse(device.last_seen) > 45000 ||
      Date.now() - Date.parse(batch.captured_at) > 120000
    )
      throw new Error("현재 앱과 규칙으로 읽은 최근 화면을 선택해 주세요.");
    const reading = JSON.parse(batch.observation).channelReadings?.[index];
    if (!reading || !["uia", "ocr"].includes(reading.source))
      throw new Error("대조할 읽기 결과가 없습니다.");
    // Re-captures of the same raw snapshot/event must not inflate the denominator.
    const key = digest(
      JSON.stringify([
        deviceId,
        scope,
        reading.source,
        reading.rawLine,
        reading.room,
        reading.code,
        reading.kind,
        reading.kind === "event" ? reading.occurredAt : "",
      ]),
    );
    const matched =
      reading.room === expectedRoom && reading.code === expectedCode ? 1 : 0;
    this.run(
      "INSERT INTO reading_checks VALUES(?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(sample_key) DO UPDATE SET expected_room=excluded.expected_room,expected_code=excluded.expected_code,matched=excluded.matched,checked_at=excluded.checked_at",
      key,
      deviceId,
      scope,
      batchId,
      reading.source,
      reading.rawLine,
      reading.room,
      reading.code,
      expectedRoom,
      expectedCode,
      matched,
      new Date().toISOString(),
    );
    this.audit("reading_checked", device.hotel_id, {
      deviceId,
      batchId,
      source: reading.source,
      matched: !!matched,
    });
    return this.readingAccuracy(deviceId, latest);
  }
  snapshot(hotel) {
    const where = hotel ? " WHERE hotel_id=?" : "";
    const args = hotel ? [hotel] : [];
    const devices = this.all(
      "SELECT id,machine,hotel_id,session_id,last_seen,version,profile_revision,pending,update_status,revoked,created_at FROM devices" +
        where,
      ...args,
    );
    const latest = devices.map((d) => {
      const row = this.get(
        "SELECT * FROM batches WHERE device_id=? AND session_id=? ORDER BY captured_at DESC LIMIT 1",
        d.id,
        d.session_id,
      );
      const batch = row
        ? { ...row, observation: JSON.parse(row.observation) }
        : null;
      const evidence = this.get(
        "SELECT id,captured_at FROM batches WHERE device_id=? AND session_id=? AND json_extract(observation,'$.image') IS NOT NULL ORDER BY captured_at DESC LIMIT 1",
        d.id,
        d.session_id,
      );
      const online = !d.revoked && Date.now() - Date.parse(d.last_seen) < 45000;
      const captureFresh =
        batch && Date.now() - Date.parse(batch.captured_at) < 60000;
      const diagnosis = diagnose(batch?.observation);
      return {
        ...d,
        online,
        captureFresh: !!captureFresh,
        status: !online
          ? "offline"
          : !captureFresh
            ? "waiting"
            : diagnosis.some((x) => !x.code.startsWith("UIA_"))
              ? "attention"
              : "collecting",
        diagnosis,
        evidence: evidence ?? null,
        verification: this.verification(d, batch),
        readingMethods: readingMethods(batch?.observation),
        readingAccuracy: this.readingAccuracy(d.id, batch),
        batch: batch
          ? {
              ...batch,
              observation: {
                ...batch.observation,
                image: undefined,
                hasImage: !!batch.observation.image,
              },
            }
          : null,
      };
    });
    const states = this.all(
      "SELECT state FROM rooms" + where + " ORDER BY room",
      ...args,
    ).map((r) => JSON.parse(r.state));
    for (const hotelId of new Set(
      latest.map((d) => d.hotel_id).filter(Boolean),
    )) {
      for (const room of this.profile(hotelId).expectedRooms ?? [])
        if (!states.some((r) => r.hotelId === hotelId && r.room === room))
          states.push({ hotelId, room });
    }
    return {
      at: new Date().toISOString(),
      devices: latest,
      hotels: this.all(
        "SELECT DISTINCT hotel_id FROM sessions ORDER BY hotel_id",
      ).map((x) => x.hotel_id),
      rooms: states.map((r) => stateForRoom(r, latest)),
      events: this.all(
        "SELECT * FROM events" + where + " ORDER BY occurred_at DESC LIMIT 100",
        ...args,
      ),
      jobs: this.all(
        "SELECT * FROM jobs" + where + " ORDER BY created_at DESC LIMIT 30",
        ...args,
      ).map((j) => ({ ...j, result: j.result ? JSON.parse(j.result) : null })),
      profiles: this.all(
        "SELECT * FROM profiles" + where + " ORDER BY created_at DESC LIMIT 20",
        ...args,
      ).map((p) => ({ ...p, body: JSON.parse(p.body) })),
      releases: this.all(
        "SELECT * FROM releases ORDER BY created_at DESC LIMIT 10",
      ),
      audit: this.all(
        "SELECT * FROM audit" + where + " ORDER BY id DESC LIMIT 40",
        ...args,
      ).map((a) => ({ ...a, detail: JSON.parse(a.detail) })),
    };
  }
  close() {
    this.db.close();
  }
}
