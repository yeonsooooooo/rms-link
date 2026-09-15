const root = document.querySelector("#app");
let state = null,
  selectedHotel = "",
  selectedDevice = null,
  view = "overview",
  live = false,
  err = "",
  modal = null;
const escape = (s) =>
  String(s ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
const when = (t) =>
  t
    ? new Date(t).toLocaleString("ko-KR", {
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
      })
    : "아직 없음";
const ago = (t) => {
  if (!t) return "연결 대기";
  const n = Math.max(0, Math.round((Date.now() - Date.parse(t)) / 1000));
  return n < 60
    ? n + "초 전"
    : n < 3600
      ? Math.floor(n / 60) + "분 전"
      : Math.floor(n / 3600) + "시간 전";
};
const labels = {
  DOOR_OPEN: "문 열림",
  DOOR_CLOSE: "문 닫힘",
  KEY_IN: "키 꽂힘",
  KEY_OUT: "키 빠짐",
  KEY_IN_GUEST: "손님 키 꽂힘",
  KEY_OUT_GUEST: "손님 키 빠짐",
  KEY_IN_CLEAN: "청소 키 꽂힘",
  KEY_OUT_CLEAN: "청소 키 빠짐",
};
const statusNames = {
  offline: "오프라인",
  waiting: "관측 대기",
  attention: "확인 필요",
  collecting: "수집 중",
  queued: "분석 대기",
  analyzing: "Codex 분석 중",
  diagnosed: "원인 분석 완료",
  needs_evidence: "확인 자료 필요",
  profile_published: "프로필 배포",
  failed: "분석 실패",
  interrupted: "분석 중단",
};
const badge = (text, color = "") =>
  `<span class="badge ${color}">${escape(text)}</span>`;
async function api(path, body) {
  const r = await fetch(path, {
    method: body === undefined ? "GET" : "POST",
    headers: { "Content-Type": "application/json", "X-Rmslink-Client": "1" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await r.json();
  if (!r.ok) throw Error(data.error || "요청 실패");
  return data;
}
function toast(text) {
  const el = document.createElement("div");
  el.className = "toast";
  el.textContent = text;
  document.body.append(el);
  setTimeout(() => el.remove(), 4500);
}
async function refresh() {
  try {
    state = await api(
      "/api/state" +
        (selectedHotel ? "?hotelId=" + encodeURIComponent(selectedHotel) : ""),
    );
    err = "";
    render();
  } catch (e) {
    err = e.message;
    render();
  }
}
const empty = (title, text, icon = "⌁", action = "") =>
  `<div class="empty"><div class="empty-icon">${icon}</div><strong>${title}</strong>${text}${action}</div>`;
function devicesTable() {
  return state.devices.length
    ? `<div class="table-scroll"><table><thead><tr><th>호텔 · 기기</th><th>연동 상태</th><th>수집 경로</th><th>마지막 연결</th><th>앱 / 프로필</th><th>전송 대기</th><th></th></tr></thead><tbody>${state.devices.map((d) => `<tr class="clickable" data-device="${d.id}"><td><strong>호텔 ${escape(d.hotel_id || "등록 중")}</strong><small>${escape(d.machine)}</small></td><td>${badge(statusNames[d.status], d.status === "collecting" ? "green" : d.status === "attention" ? "amber" : "")}</td><td>${escape(d.batch?.observation.source === "uia" ? "접근성 텍스트" : d.batch?.observation.source === "ocr" ? "화면 OCR" : "탐색 중")}<small>${escape(d.diagnosis[0]?.title || "")}</small></td><td>${ago(d.last_seen)}<small>${when(d.last_seen)}</small></td><td>${escape(d.version || "—")} <small>프로필 r${d.profile_revision}</small></td><td>${d.pending}건</td><td>↗</td></tr>`).join("")}</tbody></table></div>`
    : empty(
        "첫 호텔의 연결을 기다리고 있습니다",
        "Windows에 RmsLink를 설치하고 hotel_id를 입력하면<br>이곳에서 키텍 수집 상태를 바로 확인할 수 있습니다.",
        "⌘",
        `<div class="actions"><button data-action="download" class="primary">↓ Windows 설치 파일</button><button data-action="guide">설치 안내 보기</button></div>`,
      );
}
function roomField(field) {
  return field.value === null
    ? badge(field.quality === "unknown" ? "미확인" : "오래된 관측")
    : badge(
        { open: "열림", closed: "닫힘", inserted: "꽂힘", removed: "빠짐" }[
          field.value
        ],
        field.value === "open" || field.value === "inserted"
          ? "amber"
          : "green",
      );
}
function roomsPanel() {
  return `<section class="panel"><div class="panel-head"><div><h2>객실별 키텍 상태 <span class="sub">${state.rooms.length}개 객실</span></h2><p class="sub">문 상태와 키 상태는 각각의 관측 시각을 기준으로 표시합니다.</p></div>${badge("현재 화면 · 이벤트 근거 구분", "blue")}</div>${state.rooms.length ? `<div class="room-grid">${state.rooms.map((r) => `<div class="room"><strong>${escape(r.room)} <span class="sub">호</span></strong><div class="room-row">문 상태 ${roomField(r.door)}</div><div class="room-row">키 상태 ${roomField(r.key)}</div><small>호텔 ${escape(r.hotelId)} · ${escape(r.key.reason)}</small><small>키 근거 ${when(r.key.at)}</small><small>문 근거 ${when(r.door.at)}</small></div>`).join("")}</div>` : empty("확인된 객실 정보가 아직 없습니다", "문 열림·닫힘, 키 삽입·제거가 관측되면<br>객실별로 상태와 근거 시각을 표시합니다.", "▦")}<div class="notice">이벤트 로그는 마지막 이벤트에서 추정한 상태입니다. 현재 화면 관측은 1분, 이벤트 근거는 5분이 지나거나 기기 연결이 끊기면 ‘오래된 관측’으로 바뀝니다.</div></section>`;
}
function jobsPanel() {
  return `<section class="panel"><div class="panel-head"><div><h2>개선 진행 현황</h2><p class="sub">관측 → 원인 분석 → 검증 → 자동 업데이트</p></div>${badge(state.worker.enabled ? "자동 분석 켜짐" : "자동 분석 꺼짐", "green")}</div><div class="flow"><span>01 수집</span>→<span>02 Codex 분석</span>→<span>03 검증</span>→<span>04 배포</span></div>${
    state.jobs.length
      ? `<div class="timeline">${state.jobs
          .slice(0, 6)
          .map(
            (j) =>
              `<div class="timeline-item"><b>호텔 ${escape(j.hotel_id)} · ${statusNames[j.status] || escape(j.status)}</b><small>${when(j.updated_at)}</small><div>${escape(j.result?.summary || "맥 미니 분석 대기열에 등록되었습니다.")}</div>${j.result?.action ? `<p>${escape(j.result.action)}</p>` : ""}${j.result?.aliases?.length ? `<button data-job="${j.id}" data-action="job-details">제안 규칙과 근거 보기</button>` : ""}</div>`,
          )
          .join("")}</div>`
      : empty(
          "문제가 발견되면 분석을 시작합니다",
          "Windows 진단이 도착하면 실패 단계를 구분하고<br>호텔별 판독 규칙의 개선안을 만듭니다.",
          "↗",
        )
  }<div class="notice">확인된 샘플로 검증된 호텔 규칙은 자동 배포됩니다. 미확인 상태 어휘는 현장 확인 자료가 필요합니다. 자동 분석: 하루 최대 ${state.worker.dailyLimit}건.</div></section>`;
}
function eventPanel() {
  return `<section class="panel"><div class="panel-head"><h2>실시간 이벤트</h2><span class="sub">최근 100건</span></div>${
    state.events.length
      ? `<div class="table-scroll"><table><thead><tr><th>발생 시각</th><th>호텔</th><th>객실</th><th>관측 내용</th><th>근거</th></tr></thead><tbody>${state.events
          .slice(0, view === "events" ? 100 : 8)
          .map(
            (e) =>
              `<tr><td class="nowrap">${when(e.occurred_at)}</td><td>${escape(e.hotel_id)}</td><td><strong>${escape(e.room)}호</strong></td><td>${badge(labels[e.code] || e.code, e.code.includes("IN") || e.code === "DOOR_OPEN" ? "amber" : "green")}</td><td>${e.kind === "snapshot" ? "현재 화면" : "이벤트 로그"}<small>${escape(e.raw_line)}</small></td></tr>`,
          )
          .join("")}</tbody></table></div>`
      : empty(
          "이벤트 수신 대기",
          "실제 키텍에서 수집된 내용만 여기에 표시합니다.",
          "≋",
        )
  }</section>`;
}
function detailPanel() {
  const d = state.devices.find((x) => x.id === selectedDevice);
  if (!d) return "";
  const o = d.batch?.observation;
  return `<section class="panel"><div class="panel-head"><h2>호텔 ${escape(d.hotel_id)} · ${escape(d.machine)}</h2><button data-action="close-detail">닫기 ×</button></div><div class="detail"><div class="actions"><button class="primary" data-action="analyze" data-id="${d.id}">Codex로 지금 분석</button><button data-action="profile" data-hotel="${escape(d.hotel_id)}">호텔별 규칙 편집</button><button data-action="label" data-hotel="${escape(d.hotel_id)}">확인한 판독 샘플 등록</button></div>${d.diagnosis.map((x) => `<div class="errorbanner"><b>${escape(x.title)}</b><br>${escape(x.action)}<p>${escape(x.detail)}</p></div>`).join("")}<p>업데이트: ${escape(d.update_status || "아직 보고되지 않음")} · 화면 관측 ${when(d.batch?.captured_at)}</p>${d.evidence ? `<img src="/api/evidence/${d.evidence.id}" alt="호텔 ${escape(d.hotel_id)}에서 전송한 RMS 수집 화면">` : '<div class="notice">전송된 화면 이미지가 없습니다. 기기에서 진단 화면 공유 여부를 확인해 주세요.</div>'}<h3>판독 원문과 실패 사유</h3><pre>${escape((o?.lines ?? []).map((l) => `${l.room || "—"}  ${l.code ? labels[l.code] || l.code : l.reason}  │ ${l.text}`).join("\n") || "판독 내용이 아직 없습니다.")}</pre><h3>Windows 환경</h3><pre>${escape(JSON.stringify({ os: o?.os, source: o?.source, ocrLanguage: o?.ocrLanguage, desktopAvailable: o?.desktopAvailable, remoteSession: o?.remoteSession, windows: o?.windows, regions: o?.regions }, null, 2))}</pre></div></section>`;
}
function updatesPanel() {
  return `<section class="panel"><div class="panel-head"><h2>앱 업데이트</h2>${badge("서명 · 무결성 검사", "green")}</div>${state.releases.length ? `<div class="table-scroll"><table><thead><tr><th>버전</th><th>상태</th><th>패키지</th><th>준비 시각</th></tr></thead><tbody>${state.releases.map((r) => `<tr><td><strong>${escape(r.version)}</strong></td><td>${badge(r.status === "active" ? "자동 업데이트 제공" : r.status, "green")}</td><td>${(r.size / 1024 / 1024).toFixed(1)} MB<small>SHA-256 ${r.sha256.slice(0, 20)}…</small></td><td>${when(r.created_at)}</td></tr>`).join("")}</tbody></table></div>` : empty("검증된 앱 패키지를 준비하고 있습니다", "배포되면 설치된 RmsLink가 자동으로 최신 버전을 확인합니다.", "↓")}<div class="notice">앱은 2분마다 업데이트를 확인합니다. Windows 트레이 메뉴의 ‘지금 업데이트 확인 · 적용’으로 수동 업데이트도 가능합니다. 새 앱 실행 확인에 실패하면 이전 버전으로 복구합니다.</div></section><section class="panel"><div class="panel-head"><h2>호텔별 판독 규칙</h2></div>${state.profiles.length ? `<div class="table-scroll"><table><thead><tr><th>호텔</th><th>규칙</th><th>상태</th><th>배포 시각</th><th></th></tr></thead><tbody>${state.profiles.map((p) => `<tr><td>${escape(p.hotel_id)}</td><td>${escape(p.body.name)} · r${p.revision}</td><td>${badge(p.status === "active" ? "현재 배포" : "이전 규칙", p.status === "active" ? "green" : "")}</td><td>${when(p.created_at)}</td><td>${p.status !== "active" ? `<button data-action="rollback" data-hotel="${escape(p.hotel_id)}" data-revision="${p.revision}">이 규칙으로 복구</button>` : ""}</td></tr>`).join("")}</tbody></table></div>` : empty("공통 키텍 규칙으로 시작합니다", "호텔별 차이가 확인되면 해당 호텔에만 새 규칙을 적용합니다.", "◇")}</section>`;
}
function render() {
  if (!state) {
    root.innerHTML = `<main><div class="empty">${err ? escape(err) : "RmsLink 관제를 연결하고 있습니다…"}</div></main>`;
    return;
  }
  const devices = state.devices,
    online = devices.filter((d) => d.online).length,
    attention = devices.filter((d) => d.status === "attention").length;
  const views = {
    overview: "연동 현황",
    rooms: "객실 상태",
    events: "이벤트 로그",
    updates: "업데이트",
  };
  root.innerHTML = `<aside><div class="brand"><span class="brandmark">⌁</span>RmsLink<small>CONTROL</small></div><div class="aside-caption">WORKSPACE</div><div class="navwrap">${Object.entries(
    views,
  )
    .map(
      ([key, label], i) =>
        `<div class="nav ${view === key ? "active" : ""}" data-view="${key}" role="button" tabindex="0"><b>${["▦", "▤", "≋", "↗"][i]}</b>${label}</div>`,
    )
    .join(
      "",
    )}</div><div class="aside-bottom"><span class="dot"></span>Mac mini control center<br>키텍 연동 · 지속 개선 시스템<br><span class="sub">Windows agent v0.2</span></div></aside><main><header><span class="breadcrumb">워크스페이스 <strong>/ ${views[view]}</strong></span><span class="live"><span class="dot ${live ? "" : "warn"}"></span>${live ? "실시간 연결" : "다시 연결 중"}</span></header><div class="topline"><div><div class="eyebrow">KEYTECH INTEGRATION</div><h1>${view === "overview" ? "호텔 키텍 연동 현황" : views[view]}</h1><p class="sub">각 호텔의 연결부터 객실 상태, 개선과 업데이트까지 한곳에서 확인하세요.</p></div><div class="actions"><button data-action="refresh">↻ 새로고침</button><button data-action="download" class="primary">↓ Windows 설치 파일</button></div></div>${err ? `<div class="errorbanner">${escape(err)}</div>` : ""}<div class="metrics">${[
    ["연결된 기기", online, `등록된 기기 ${devices.length}대`, "⌘"],
    [
      "키텍 수집 중",
      devices.filter((d) => d.status === "collecting").length,
      "수집 경로와 진단 정상",
      "⌁",
    ],
    ["확인이 필요한 기기", attention, "실패 원인과 조치 확인", "△"],
    ["확인된 객실", state.rooms.length, "현재 상태와 근거 시각 추적", "▦"],
  ]
    .map(
      ([label, n, foot, icon]) =>
        `<div class="metric"><div class="metric-top">${label}<span class="metric-icon">${icon}</span></div><div class="metric-value">${n}<small>${label === "확인된 객실" ? "객실" : "대"}</small></div><div class="metric-foot">${foot}</div></div>`,
    )
    .join(
      "",
    )}</div><section class="panel"><div class="panel-head"><div><h2>호텔 · 연결 기기</h2><p class="sub">${online}대 온라인 · ${devices.length - online}대 연결 대기/오프라인</p></div><div class="toolbar"><select id="hotel-filter" aria-label="호텔 필터"><option value="">전체 호텔</option>${state.hotels.map((h) => `<option ${h === selectedHotel ? "selected" : ""} value="${escape(h)}">호텔 ${escape(h)}</option>`).join("")}</select></div></div>${devicesTable()}</section>${detailPanel()}${view === "overview" ? `<div class="grid2">${roomsPanel()}${jobsPanel()}</div>${eventPanel()}` : view === "rooms" ? roomsPanel() : view === "events" ? eventPanel() : updatesPanel() + jobsPanel()}<div class="footer"><span>RmsLink · 호텔별 관측 데이터와 업데이트 이력을 보관합니다.</span><span>마지막 갱신 ${when(state.at)}</span></div></main>`;
}
function openModal(title, body, save) {
  modal = document.createElement("div");
  modal.className = "modal-backdrop";
  modal.innerHTML = `<section class="modal"><h2>${title}</h2>${body}<div class="actions"><button data-close>닫기</button>${save ? '<button class="primary" data-save>저장 · 검증</button>' : ""}</div></section>`;
  document.body.append(modal);
  modal.querySelector("[data-close]").onclick = () => {
    modal.remove();
    modal = null;
  };
  if (save)
    modal.querySelector("[data-save]").onclick = async () => {
      try {
        await save(modal);
        modal.remove();
        modal = null;
        await refresh();
      } catch (e) {
        toast(e.message);
      }
    };
}
root.addEventListener("change", (e) => {
  if (e.target.id === "hotel-filter") {
    selectedHotel = e.target.value;
    selectedDevice = null;
    refresh();
  }
});
root.addEventListener("click", async (e) => {
  const el = e.target.closest("[data-action],[data-view],[data-device]");
  if (!el) return;
  if (el.dataset.view) {
    view = el.dataset.view;
    render();
    return;
  }
  if (el.dataset.device) {
    selectedDevice = el.dataset.device;
    render();
    return;
  }
  try {
    switch (el.dataset.action) {
      case "refresh":
        await refresh();
        break;
      case "close-detail":
        selectedDevice = null;
        render();
        break;
      case "download":
        if (!state.installer) {
          toast("설치 파일 빌드를 준비하고 있습니다.");
          break;
        }
        const link = await api("/api/installer-link");
        openModal(
          "Windows에 RmsLink 설치",
          `<p class="sub">설치 파일을 Windows PC로 옮겨 실행한 뒤 hotel_id를 입력하세요. 등록권은 발급 후 7일간, 최대 100대에 사용할 수 있습니다.</p><label>Windows용 다운로드 주소<input readonly id="download-link" value="${escape(link.url)}"></label><div class="actions"><button id="copy-link">주소 복사</button><a class="button primary" href="/api/installer">이 Mac에 다운로드</a></div><div class="notice">현재 현장 시험본은 Windows 공인 코드 서명이 없습니다. 보안 차단이 나타나면 차단 내용을 확인해 주세요. 한국어 OCR이 없으면 앱에서 진단 원인이 표시됩니다.</div>`,
        );
        modal.querySelector("#copy-link").onclick = async () => {
          await navigator.clipboard.writeText(link.url);
          toast("설치 주소를 복사했습니다.");
        };
        break;
      case "guide":
        openModal(
          "설치와 현장 확인",
          `<div class="detail"><p>1. Windows 10 1809 이상, x64 PC에서 설치 파일 실행</p><p>2. 호텔 ID 입력 → 호텔 연결 시작</p><p>3. 키텍/RMS 창이 하나이면 자동 탐색합니다. 필요하면 로그 영역을 직접 지정하세요.</p><p>4. 시험 객실의 문을 열고 닫고, 키를 넣고 빼며 대시보드 이벤트를 대조하세요.</p><p>5. 맞지 않는 원문은 ‘확인한 판독 샘플 등록’으로 기록합니다. 검증된 호텔 규칙은 자동 업데이트됩니다.</p></div>`,
        );
        break;
      case "analyze":
        const r = await api("/api/analyze", { deviceId: el.dataset.id });
        toast(
          r.queued ? "Codex 분석을 요청했습니다." : r.reason || "분석 대기 중",
        );
        await refresh();
        break;
      case "profile": {
        const hotel = el.dataset.hotel;
        const active = state.profiles.find(
          (p) => p.hotel_id === hotel && p.status === "active",
        )?.body ?? {
          revision: 1,
          hotelId: hotel,
          name: "호텔 " + hotel + " 키텍",
          mode: "events",
          roomPattern: "(?<![\\p{L}\\d:])(\\d{3,4})\\s*호?(?![\\d:])",
          aliases: {},
          roomMap: {},
          pollMs: 1500,
          ocrScale: 3,
        };
        const p = {
          ...active,
          hotelId: hotel,
          revision:
            Math.max(
              1,
              ...state.profiles
                .filter((x) => x.hotel_id === hotel)
                .map((x) => x.revision),
            ) + 1,
        };
        openModal(
          "호텔 " + escape(hotel) + " 판독 규칙",
          `<p class="sub">events는 시각이 있는 로그, snapshot은 현재 상태 화면입니다. aliases는 어휘→이벤트 코드, roomMap은 화면 번호→객실 번호입니다. 저장하면 공통·호텔 샘플 검증 후 배포합니다.</p><textarea id="profile-json" spellcheck="false">${escape(JSON.stringify(p, null, 2))}</textarea>`,
          async (m) => {
            await api(
              "/api/profiles",
              JSON.parse(m.querySelector("textarea").value),
            );
            toast("프로필 검증 및 배포 완료");
          },
        );
        break;
      }
      case "label":
        openModal(
          "현장에서 확인한 판독 샘플",
          `<p class="sub">실제 키텍 동작과 대조한 원문만 등록하세요. 확인 자료가 새 호텔 어휘의 자동 업데이트 기준이 됩니다.</p><label>호텔 ID<input id="label-hotel" value="${escape(el.dataset.hotel)}" readonly></label><label>판독 원문<input id="label-line" placeholder="101 고객키투입 12:03:10"></label><label>실제 객실 번호<input id="label-room" placeholder="101"></label><label>확인한 실제 상태<select id="label-code">${Object.entries(
            labels,
          )
            .map(([code, label]) => `<option value="${code}">${label}</option>`)
            .join("")}</select></label>`,
          async (m) => {
            await api("/api/labels", {
              hotelId: m.querySelector("#label-hotel").value,
              line: m.querySelector("#label-line").value,
              room: m.querySelector("#label-room").value,
              code: m.querySelector("#label-code").value,
            });
            toast(
              "샘플을 등록했습니다. 지금 분석을 누르면 새 규칙을 검증합니다.",
            );
          },
        );
        break;
      case "rollback":
        await api("/api/rollback", {
          hotelId: el.dataset.hotel,
          revision: Number(el.dataset.revision),
        });
        toast("이전 규칙으로 새 버전을 배포했습니다.");
        await refresh();
        break;
      case "job-details": {
        const j = state.jobs.find((j) => j.id === el.dataset.job);
        openModal(
          "Codex 분석 근거",
          `<div class="detail"><pre>${escape(JSON.stringify(j.result, null, 2))}</pre></div>`,
        );
        break;
      }
    }
  } catch (e) {
    toast(e.message);
  }
});
try {
  await api("/api/bootstrap", {});
  await refresh();
  const events = new EventSource("/api/events");
  events.addEventListener("ready", () => {
    live = true;
    render();
  });
  let pending;
  events.addEventListener("change", () => {
    clearTimeout(pending);
    pending = setTimeout(refresh, 180);
  });
  events.onerror = () => {
    live = false;
    render();
  };
  events.onopen = () => {
    live = true;
    render();
  };
} catch (e) {
  err = e.message;
  render();
}
setInterval(() => {
  if (!modal) refresh();
}, 15000);
