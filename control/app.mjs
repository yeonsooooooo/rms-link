import { createServer } from "node:http";
import { readFileSync, existsSync, statSync, createReadStream } from "node:fs";
import { join, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { z } from "zod";
import { Store, safeEqual } from "./store.mjs";
import { batchSchema, id, digest, profileSchema, codes } from "./domain.mjs";
import { ReleaseSync } from "./releases.mjs";
import { ImprovementWorker } from "./improve.mjs";
const publicDir = fileURLToPath(new URL("./public/", import.meta.url));
export async function createApp({
  directory = resolve(".data"),
  adminPort = 18760,
  agentPort = 18761,
  autoAnalyze = true,
  autoRelease = false,
  corePath = process.env.RMSLINK_CORE,
  codex = process.env.RMSLINK_CODEX ?? "/Users/ys/.local/bin/codex",
} = {}) {
  const store = new Store(directory),
    subscribers = new Set(),
    rate = new Map();
  let ownOrigin;
  const broadcast = () => {
    for (const res of subscribers)
      if (!res.write("event: change\ndata: {}\n\n")) {
        subscribers.delete(res);
        res.destroy();
      }
  };
  const worker = new ImprovementWorker({
    store,
    broadcast,
    enabled: autoAnalyze,
    corePath,
    codex,
  });
  const releases = new ReleaseSync(store, broadcast, {
    enabled: autoRelease,
    branch: process.env.RMSLINK_RELEASE_BRANCH ?? "codex/rmslink-control",
  });
  const retention = setInterval(() => {
    const imageBefore = new Date(Date.now() - 86400000).toISOString(),
      dataBefore = new Date(Date.now() - 14 * 86400000).toISOString();
    store.run(
      "UPDATE batches SET observation=json_remove(observation,'$.image') WHERE captured_at<? AND json_extract(observation,'$.image') IS NOT NULL",
      imageBefore,
    );
    store.run(
      "DELETE FROM batches WHERE captured_at<? AND id NOT IN (SELECT batch_id FROM jobs)",
      dataBefore,
    );
    store.run(
      "DELETE FROM events WHERE received_at<?",
      new Date(Date.now() - 90 * 86400000).toISOString(),
    );
  }, 3600000);
  retention.unref();
  const heartbeat = setInterval(() => {
    broadcast();
    rate.clear();
  }, 15000);
  heartbeat.unref();
  const handler = (admin) => async (req, res) => {
    res.setHeader("Cache-Control", "no-store");
    res.setHeader("X-Content-Type-Options", "nosniff");
    res.setHeader("Referrer-Policy", "no-referrer");
    res.setHeader(
      "Content-Security-Policy",
      "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'",
    );
    const json = (data, status = 200) => {
      res.writeHead(status, {
        "Content-Type": "application/json; charset=utf-8",
      });
      res.end(JSON.stringify(data));
    };
    async function body() {
      let n = 0;
      const chunks = [];
      for await (const chunk of req) {
        n += chunk.length;
        if (n > 1200000) throw new Error("요청 크기 초과");
        chunks.push(chunk);
      }
      return JSON.parse(Buffer.concat(chunks).toString() || "{}");
    }
    try {
      const url = new URL(req.url, "http://localhost");
      const token = req.headers.authorization?.replace(/^Bearer /, "");
      if (admin) {
        if (
          req.headers.host !== new URL(ownOrigin).host ||
          (req.headers.origin && req.headers.origin !== ownOrigin)
        ) {
          json({ error: "허용하지 않는 출처" }, 403);
          return;
        }
        if (url.pathname === "/api/bootstrap" && req.method === "POST") {
          if (
            req.headers["x-rmslink-client"] !== "1" ||
            req.headers["sec-fetch-site"] === "cross-site"
          ) {
            json({ error: "맥 미니 관제에서 열어 주세요." }, 403);
            return;
          }
          res.setHeader(
            "Set-Cookie",
            `rmslink_session=${store.adminToken}; Path=/; HttpOnly; SameSite=Strict`,
          );
          json({ ok: true });
          return;
        }
        if (url.pathname.startsWith("/api/")) {
          const cookie = req.headers.cookie?.match(
            /(?:^|;\s*)rmslink_session=([^;]+)/,
          )?.[1];
          if (
            !safeEqual(token, store.adminToken) &&
            !safeEqual(cookie, store.adminToken)
          ) {
            json({ error: "인증 필요" }, 401);
            return;
          }
          if (req.method !== "GET" && req.headers["x-rmslink-client"] !== "1") {
            json({ error: "요청 헤더 필요" }, 403);
            return;
          }
          if (url.pathname === "/api/events" && req.method === "GET") {
            res.writeHead(200, {
              "Content-Type": "text/event-stream",
              Connection: "keep-alive",
            });
            res.write("event: ready\ndata: {}\n\n");
            subscribers.add(res);
            req.on("close", () => subscribers.delete(res));
            return;
          }
          if (url.pathname === "/api/state" && req.method === "GET") {
            const hotel = url.searchParams.get("hotelId");
            json({
              ...store.snapshot(hotel ? id.parse(hotel) : undefined),
              worker: worker.status(),
              installer: existsSync(join(directory, "installer.exe"))
                ? { available: true }
                : null,
            });
            return;
          }
          if (url.pathname === "/api/installer" && req.method === "GET") {
            const f = join(directory, "installer.exe");
            if (!existsSync(f)) {
              json({ error: "설치 파일 빌드 중" }, 404);
              return;
            }
            res.writeHead(200, {
              "Content-Type": "application/octet-stream",
              "Content-Disposition": 'attachment; filename="RmsLink-Setup.exe"',
              "Content-Length": statSync(f).size,
            });
            createReadStream(f).pipe(res);
            return;
          }
          if (url.pathname === "/api/installer-link" && req.method === "GET") {
            json({
              url: `https://ys-macmini.tail984bfd.ts.net:8443/download/${store.downloadToken}/RmsLink-Setup.exe`,
            });
            return;
          }
          if (
            url.pathname.startsWith("/api/evidence/") &&
            req.method === "GET"
          ) {
            const bid = z.uuid().parse(url.pathname.split("/")[3]);
            const b = store.get(
              "SELECT observation FROM batches WHERE id=?",
              bid,
            );
            const encoded = b && JSON.parse(b.observation).image;
            if (!encoded) {
              json({ error: "증거 이미지 없음" }, 404);
              return;
            }
            res.writeHead(200, { "Content-Type": "image/jpeg" });
            res.end(Buffer.from(encoded, "base64"));
            return;
          }
          if (url.pathname === "/api/analyze" && req.method === "POST") {
            const b = z.object({ deviceId: z.uuid() }).parse(await body());
            json(worker.enqueue(b.deviceId, true));
            broadcast();
            return;
          }
          if (url.pathname === "/api/labels" && req.method === "POST") {
            const b = z
              .object({
                hotelId: id,
                line: z.string().min(1).max(1000),
                room: z.string().min(1).max(30),
                code: z.enum(codes),
              })
              .parse(await body());
            store.run(
              "INSERT INTO labels(hotel_id,line,room,code,created_at) VALUES(?,?,?,?,?)",
              b.hotelId,
              b.line,
              b.room,
              b.code,
              new Date().toISOString(),
            );
            store.audit("label_added", b.hotelId, {
              room: b.room,
              code: b.code,
            });
            json({ ok: true });
            broadcast();
            return;
          }
          if (url.pathname === "/api/profiles" && req.method === "POST") {
            const b = profileSchema.parse(await body());
            if (b.hotelId === "*")
              throw new Error("대시보드 변경은 호텔별 프로필을 지정해 주세요.");
            const profile = await worker.publish(b, "operator");
            json(profile);
            broadcast();
            return;
          }
          if (url.pathname === "/api/rollback" && req.method === "POST") {
            const b = z
              .object({ hotelId: id, revision: z.number().int().positive() })
              .parse(await body());
            const old = store.get(
              "SELECT body FROM profiles WHERE hotel_id=? AND revision=?",
              b.hotelId,
              b.revision,
            );
            if (!old) throw new Error("이전 프로필을 찾을 수 없습니다.");
            const p = JSON.parse(old.body);
            p.revision =
              (store.get(
                "SELECT MAX(revision) AS n FROM profiles WHERE hotel_id=?",
                b.hotelId,
              )?.n ?? 1) + 1;
            json(await worker.publish(p, "rollback"));
            broadcast();
            return;
          }
          if (url.pathname === "/api/revoke" && req.method === "POST") {
            const b = z.object({ deviceId: z.uuid() }).parse(await body());
            store.run("UPDATE devices SET revoked=1 WHERE id=?", b.deviceId);
            store.audit("device_revoked", "", { deviceId: b.deviceId });
            json({ ok: true });
            broadcast();
            return;
          }
          json({ error: "API 없음" }, 404);
          return;
        }
        if (req.method !== "GET") {
          json({ error: "지원하지 않는 요청" }, 405);
          return;
        }
        const files = {
          "/": "index.html",
          "/app.js": "app.js",
          "/style.css": "style.css",
        };
        const file = files[url.pathname];
        if (!file) {
          json({ error: "없음" }, 404);
          return;
        }
        res.writeHead(200, {
          "Content-Type": {
            ".html": "text/html; charset=utf-8",
            ".js": "text/javascript; charset=utf-8",
            ".css": "text/css; charset=utf-8",
          }[extname(file)],
        });
        res.end(readFileSync(join(publicDir, file)));
        return;
      }
      // Public listener has no dashboard or administrator routes.
      const key = digest(token ?? req.socket.remoteAddress ?? "anon");
      const used = (rate.get(key) ?? 0) + 1;
      rate.set(key, used);
      if (used > 180) {
        json({ error: "너무 많은 요청" }, 429);
        return;
      }
      if (url.pathname === "/health" && req.method === "GET") {
        json({ ok: true, service: "RmsLink" });
        return;
      }
      if (
        url.pathname === `/download/${store.downloadToken}/RmsLink-Setup.exe` &&
        req.method === "GET"
      ) {
        const f = join(directory, "installer.exe");
        if (!existsSync(f)) {
          json({ error: "없음" }, 404);
          return;
        }
        res.writeHead(200, {
          "Content-Type": "application/octet-stream",
          "Content-Disposition": 'attachment; filename="RmsLink-Setup.exe"',
          "Content-Length": statSync(f).size,
        });
        createReadStream(f).pipe(res);
        return;
      }
      if (url.pathname === "/agent/enroll" && req.method === "POST") {
        const b = z
          .object({
            deviceId: z.uuid(),
            enrollmentCode: z.string().max(100),
            machine: z.string().min(1).max(100),
          })
          .parse(await body());
        if (!token || !/^[a-f0-9]{64}$/.test(token)) {
          json({ error: "기기 키 필요" }, 401);
          return;
        }
        const existing = store.get(
          "SELECT * FROM devices WHERE id=?",
          b.deviceId,
        );
        if (existing) {
          if (
            existing.revoked ||
            !safeEqual(existing.token_hash, digest(token))
          ) {
            json({ error: "기기 인증 거부" }, 403);
            return;
          }
          json({ ok: true });
          return;
        }
        store.transaction(() => {
          const code = store.get(
            "SELECT * FROM enrollments WHERE hash=?",
            digest(b.enrollmentCode),
          );
          if (
            !code ||
            code.remaining <= 0 ||
            Date.parse(code.expires_at) < Date.now()
          )
            throw new Error(
              "설치 등록권 만료: 맥 미니에서 새 설치 파일 발급 필요",
            );
          store.run(
            "UPDATE enrollments SET remaining=remaining-1 WHERE hash=?",
            code.hash,
          );
          store.run(
            "INSERT INTO devices(id,token_hash,machine,created_at) VALUES(?,?,?,?)",
            b.deviceId,
            digest(token),
            b.machine,
            new Date().toISOString(),
          );
        });
        store.audit("device_enrolled", "", {
          deviceId: b.deviceId,
          machine: b.machine,
        });
        json({ ok: true }, 201);
        broadcast();
        return;
      }
      const device = store.authenticate(token);
      if (!device) {
        json({ error: "기기 인증 필요" }, 401);
        return;
      }
      if (url.pathname === "/agent/heartbeat" && req.method === "POST") {
        const b = z
          .object({
            deviceId: z.uuid(),
            hotelId: id,
            sessionId: z.uuid(),
            version: z.string().regex(/^\d+\.\d+\.\d+$/),
            profileRevision: z.number().int().positive(),
            pending: z.number().int().nonnegative(),
            updateStatus: z.string().max(1000),
          })
          .parse(await body());
        if (b.deviceId !== device.id) throw new Error("기기 ID 불일치");
        store.transaction(() => {
          const session = store.get(
            "SELECT * FROM sessions WHERE id=?",
            b.sessionId,
          );
          if (
            session &&
            (session.device_id !== device.id || session.hotel_id !== b.hotelId)
          )
            throw new Error("세션 호텔 불일치");
          store.run(
            "INSERT OR IGNORE INTO sessions VALUES(?,?,?)",
            b.sessionId,
            device.id,
            b.hotelId,
          );
          store.run(
            "UPDATE devices SET hotel_id=?,session_id=?,last_seen=?,version=?,profile_revision=?,pending=?,update_status=? WHERE id=?",
            b.hotelId,
            b.sessionId,
            new Date().toISOString(),
            b.version,
            b.profileRevision,
            b.pending,
            b.updateStatus,
            device.id,
          );
        });
        json({ ok: true });
        broadcast();
        return;
      }
      if (url.pathname === "/agent/observations" && req.method === "POST") {
        const b = batchSchema.parse(await body());
        if (b.deviceId !== device.id) throw new Error("기기 ID 불일치");
        if (Date.parse(b.observation.capturedAt) > Date.now() + 120000)
          throw new Error("Windows 시계가 미래입니다.");
        const old = store.get(
          "SELECT device_id,hotel_id FROM batches WHERE id=?",
          b.id,
        );
        if (old && (old.device_id !== device.id || old.hotel_id !== b.hotelId))
          throw new Error("전송 ID 충돌");
        store.ingest(b);
        json({ ack: b.id });
        broadcast();
        worker.enqueue(device.id);
        return;
      }
      if (url.pathname === "/agent/updates" && req.method === "GET") {
        const hotel = id.parse(url.searchParams.get("hotelId"));
        if (
          !store.get(
            "SELECT id FROM sessions WHERE device_id=? AND hotel_id=?",
            device.id,
            hotel,
          )
        ) {
          json({ error: "호텔 세션 필요" }, 403);
          return;
        }
        const r = store
          .all("SELECT * FROM releases WHERE status='active'")
          .sort((a, b) => compareVersions(b.version, a.version))[0];
        json(
          store.signed({
            profile: store.profile(hotel),
            release: r
              ? {
                  version: r.version,
                  sha256: r.sha256,
                  size: r.size,
                  path: "agent/packages/" + r.sha256,
                }
              : null,
          }),
        );
        return;
      }
      const match = url.pathname.match(/^\/agent\/packages\/([a-f0-9]{64})$/);
      if (match && req.method === "GET") {
        const r = store.get(
          "SELECT * FROM releases WHERE sha256=? AND status='active'",
          match[1],
        );
        const f = join(directory, "packages", match[1] + ".zip");
        if (!r || !existsSync(f)) {
          json({ error: "패키지 없음" }, 404);
          return;
        }
        res.writeHead(200, {
          "Content-Type": "application/zip",
          "Content-Length": statSync(f).size,
        });
        createReadStream(f).pipe(res);
        return;
      }
      json({ error: "없음" }, 404);
    } catch (e) {
      if (res.headersSent) {
        res.destroy();
        return;
      }
      json(
        { error: e instanceof z.ZodError ? "입력 형식 오류" : e.message },
        400,
      );
    }
  };
  const admin = createServer(handler(true)),
    agent = createServer(handler(false));
  for (const s of [admin, agent]) {
    s.requestTimeout = 30000;
    s.headersTimeout = 10000;
    s.maxConnections = 100;
  }
  const listen = (s, p) =>
    new Promise((r, j) => {
      s.once("error", j);
      s.listen(p, "127.0.0.1", r);
    });
  await listen(admin, adminPort);
  ownOrigin = `http://127.0.0.1:${admin.address().port}`;
  try {
    await listen(agent, agentPort);
  } catch (e) {
    admin.close();
    store.close();
    throw e;
  }
  worker.start();
  releases.start();
  return {
    store,
    worker,
    admin,
    agent,
    url: ownOrigin,
    agentUrl: `http://127.0.0.1:${agent.address().port}`,
    async close() {
      clearInterval(heartbeat);
      clearInterval(retention);
      await releases.stop();
      await worker.stop();
      for (const s of subscribers) s.end();
      await Promise.all(
        [admin, agent].map(
          (s) =>
            new Promise((r) => {
              s.close(r);
              s.closeIdleConnections();
            }),
        ),
      );
      store.close();
    },
  };
}
function compareVersions(a, b) {
  const x = a.split(".").map(Number),
    y = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}
