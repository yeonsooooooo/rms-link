import { randomBytes } from "node:crypto";
import { safeEqual } from "./store.mjs";
export class DashboardAuth {
  constructor(store) {
    this.key = store.secret("dashboard-access.key");
    this.sessions = new Map();
    this.attempts = [];
  }
  login(password) {
    const now = Date.now();
    this.attempts = this.attempts.filter((t) => now - t < 15 * 60_000);
    if (this.attempts.length >= 12)
      return {
        error: "로그인 시도가 많습니다. 15분 후 다시 시도해 주세요.",
        status: 429,
      };
    if (!safeEqual(password, this.key)) {
      this.attempts.push(now);
      return { error: "접속 코드가 올바르지 않습니다.", status: 401 };
    }
    this.attempts = [];
    for (const [key, expiry] of this.sessions)
      if (expiry <= now) this.sessions.delete(key);
    if (this.sessions.size >= 100)
      this.sessions.delete(this.sessions.keys().next().value);
    const token = randomBytes(32).toString("hex");
    this.sessions.set(token, now + 12 * 3600_000);
    return { token };
  }
  valid(token) {
    return (
      typeof token === "string" && (this.sessions.get(token) ?? 0) > Date.now()
    );
  }
  logout(token) {
    this.sessions.delete(token);
  }
}
