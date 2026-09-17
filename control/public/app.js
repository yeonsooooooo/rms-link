const sourceName = (source) =>
  ({
    uia: "글자 직접 읽기",
    ocr: "사진에서 읽기(OCR)",
    hybrid: "직접 읽기 · OCR도 일치",
  })[source] ?? "읽기 방식 기록 없음";
const root = document.querySelector("#app");
let state = null,
  selectedHotel = "",
  selectedDevice = null,
  view = "overview",
  live = false,
  err = "",
  modal = null;
let loginRequired = false,
  remote = false,
  events = null,
  refreshing = false;
const escape = (s) =>
  String(s ?? "").replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );
const analysisText = (value) =>
  String(value ?? "")
    .replace(/\bcodex\b/gi, "분석 엔진")
    .replace(/\bmac[ -]?mini\b|맥\s*미니/gi, "관리 서버");
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
  analyzing: "화면 분석 중",
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
  if (!r.ok) {
    if (r.status === 401 && path !== "/api/login") {
      loginRequired = true;
      state = null;
      events?.close();
    }
    throw Error(data.error || "요청 실패");
  }
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
  if (loginRequired || refreshing) return;
  refreshing = true;
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
  } finally {
    refreshing = false;
  }
}
const empty = (title, text, icon = "⌁", action = "") =>
  `<div class="empty"><div class="empty-icon">${icon}</div><strong>${title}</strong>${text}${action}</div>`;
function devicesTable() {
  return state.devices.length
    ? `<div class="table-scroll"><table><thead><tr><th>호텔 · 기기</th><th>연동 상태</th><th>수집 경로</th><th>마지막 연결</th><th>앱 / 프로필</th><th>전송 대기</th><th></th></tr></thead><tbody>${state.devices.map((d) => `<tr class="clickable" data-device="${d.id}"><td><strong>호텔 ${escape(d.hotel_id || "등록 중")}</strong><small>${escape(d.machine)}</small></td><td>${badge(statusNames[d.status], d.status === "collecting" ? "green" : d.status === "attention" ? "amber" : "")}</td><td>${escape(d.readingMethods?.title || sourceName(d.batch?.observation.source))}<small>${escape(d.diagnosis[0]?.title || "")}</small></td><td>${ago(d.last_seen)}<small>${when(d.last_seen)}</small></td><td>${escape(d.version || "—")} <small>프로필 r${d.profile_revision}</small></td><td>${d.pending}건</td><td>↗</td></tr>`).join("")}</tbody></table></div>`
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
  return `<section class="panel"><div class="panel-head"><div><h2>객실별 키텍 상태 <span class="sub">${state.rooms.length}개 객실</span></h2><p class="sub">문 상태와 키 상태는 각각의 관측 시각을 기준으로 표시합니다.</p></div>${badge("현재 화면 · 이벤트 근거 구분", "blue")}</div>${state.rooms.length ? `<div class="room-grid">${state.rooms.map((r) => `<div class="room"><strong>${escape(r.room)} <span class="sub">호</span></strong><div class="room-row">문 상태 ${roomField(r.door)}</div><div class="room-row">키 상태 ${roomField(r.key)}</div><small>호텔 ${escape(r.hotelId)} · ${escape(r.key.reason)}</small><small>키 근거 ${when(r.key.at)} · ${sourceName(r.key.source)}</small><small>문 근거 ${when(r.door.at)} · ${sourceName(r.door.source)}</small></div>`).join("")}</div>` : empty("확인된 객실 정보가 아직 없습니다", "문 열림·닫힘, 키 삽입·제거가 관측되면<br>객실별로 상태와 근거 시각을 표시합니다.", "▦")}<div class="notice">이벤트 로그는 마지막 이벤트에서 추정한 상태입니다. 현재 화면 관측은 1분, 이벤트 근거는 5분이 지나거나 기기 연결이 끊기면 ‘오래된 관측’으로 바뀝니다.</div></section>`;
}
function jobsPanel() {
  return `<section class="panel"><div class="panel-head"><div><h2>개선 진행 현황</h2><p class="sub">관측 → 원인 분석 → 검증 → 자동 업데이트</p></div>${badge(state.worker.enabled ? "자동 분석 켜짐" : "자동 분석 꺼짐", "green")}</div><div class="flow"><span>01 수집</span>→<span>02 화면 분석</span>→<span>03 검증</span>→<span>04 배포</span></div>${
    state.jobs.length
      ? `<div class="timeline">${state.jobs
          .slice(0, 6)
          .map(
            (j) =>
              `<div class="timeline-item"><b>호텔 ${escape(j.hotel_id)} · ${statusNames[j.status] || escape(j.status)}</b><small>${when(j.updated_at)}</small><div>${escape(analysisText(j.result?.summary || "관리 서버 분석 대기열에 등록되었습니다."))}</div>${j.result?.action ? `<p>${escape(analysisText(j.result.action))}</p>` : ""}${j.result?.aliases?.length ? `<button data-job="${j.id}" data-action="job-details">제안 규칙과 근거 보기</button>` : ""}</div>`,
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
              `<tr><td class="nowrap">${when(e.occurred_at)}</td><td>${escape(e.hotel_id)}</td><td><strong>${escape(e.room)}호</strong></td><td>${badge(labels[e.code] || e.code, e.code.includes("IN") || e.code === "DOOR_OPEN" ? "amber" : "green")}</td><td>${e.kind === "snapshot" ? "현재 화면" : "이벤트 로그"}<small>${sourceName(e.source)}</small><small>${escape(e.raw_line)}</small></td></tr>`,
          )
          .join("")}</tbody></table></div>`
      : empty(
          "이벤트 수신 대기",
          "실제 키텍에서 수집된 내용만 여기에 표시합니다.",
          "≋",
        )
  }</section>`;
}
function hotelProfile(hotel) {
  return (
    state.profiles.find((p) => p.hotel_id === hotel && p.status === "active")
      ?.body ?? {
      revision: 1,
      hotelId: hotel,
      name: "호텔 " + hotel + " 키텍",
      mode: "events",
      roomPattern: "(?<![\\p{L}\\d:])(\\d{3,4})\\s*호?(?![\\d:])",
      aliases: {},
      roomMap: {},
      expectedRooms: [],
      liveClockPattern: "",
      pollMs: 1500,
      ocrScale: 3,
    }
  );
}
function nextProfile(hotel) {
  return {
    ...hotelProfile(hotel),
    hotelId: hotel,
    revision:
      Math.max(
        1,
        ...state.profiles
          .filter((p) => p.hotel_id === hotel)
          .map((p) => p.revision),
      ) + 1,
  };
}
function readingStatus(d) {
  if (!state.capabilities?.includes("screen-reading-v2")) return "";
  const o = d.batch?.observation,
    c = o?.coverage,
    v = d.verification;
  const missing = (c?.expectedRooms ?? []).filter(
    (r) => !c.observedRooms.includes(r),
  );
  const steps = v?.required ?? ["DOOR_OPEN", "DOOR_CLOSE", "KEY_IN", "KEY_OUT"];
  return `<div class="notice"><b>화면 연결 확인</b><p>${escape(o?.application?.vendor ?? "제조사 미선택")} · ${escape(o?.application?.product ?? "제품 확인 대기")} ${escape(o?.application?.version ?? "")}</p>
    <p>${c ? `${c.mode === "snapshot" ? "현재 상태표" : "이벤트 로그"} · 이번 화면 ${c.observedRooms.length}개 객실 / ${c.expectedRooms.length ? `등록 ${c.expectedRooms.length}개 객실` : "전체 객실 목록 미등록"}` : "새 수집 진단 대기"}</p>
    ${missing.length ? `<p>이번 화면에서 확인되지 않은 객실: ${escape(missing.join(", "))}</p>` : ""}
    <p>${v?.room ? `시험 객실 ${escape(v.room)} · ` : ""}${steps.map((s) => badge(labels[s], v?.completed?.includes(s) ? "green" : "")).join(" ")}</p>
    <p>${v?.status === "sample_verified" ? "시험 객실의 네 가지 동작을 현장에서 대조했습니다. 전체 객실과 다른 버전의 호환성은 별도로 확인해야 합니다." : "실제 문 열기·닫기와 손님 키 꽂기·빼기를 차례로 실행하고 수신 값과 대조해 주세요."}</p>
    ${v?.profileCurrent === false ? "<p>새 호텔 규칙을 기기에 적용한 뒤 다시 확인하세요.</p>" : ""}</div>
    ${(o?.warnings ?? []).map((w) => `<div class="notice">${escape(w.includes(":") ? w.slice(w.indexOf(":") + 1).trim() : w)}</div>`).join("")}`;
}
function methodsPanel(d) {
  const m = d.readingMethods,
    o = d.batch?.observation;
  if (!m) return "";
  const current = d.online && d.captureFresh;
  return `<div class="notice reading-methods"><h3>어떻게 읽었나요?</h3>
    ${!current ? "<p><b>이전 수집 결과입니다. 다시 연결된 뒤 현재 화면을 확인하세요.</b></p>" : ""}
    <b>${escape(m.title)}</b><p>${escape(m.detail)}</p><p>${escape(m.ocrDetail || "")}</p>
    ${m.uia ? `<div class="table-scroll"><table><thead><tr><th>읽기 방법</th><th>가져온 글자</th><th>해석한 상태</th><th>사용한 상태</th></tr></thead><tbody>${["uia", "ocr"].map((source) => `<tr><td>${sourceName(source)}</td><td>${m[source].lineCount}줄</td><td>${m[source].candidateCount}개</td><td>${m[source].acceptedCount}개</td></tr>`).join("")}</tbody></table></div><p>이번 화면의 건수입니다. 두 방법이 같은 상태를 읽으면 양쪽에 표시됩니다. 확인 대기·충돌·오류가 있으면 사용하지 않습니다.</p>` : ""}
    <h3>사람이 대조한 샘플의 정확도</h3>
    <p>실제 객실 번호와 문·키 상태를 확인해 맞음과 틀림을 모두 기록하세요. 자동 대조만으로는 정확도를 계산하지 않습니다.</p>
    <div class="table-scroll"><table><thead><tr><th>읽기 방법</th><th>대조한 샘플</th><th>맞음 / 틀림</th><th>샘플 정답률</th></tr></thead><tbody>${(d.readingAccuracy ?? []).map((a) => `<tr><td>${sourceName(a.source)}</td><td>${a.total}개</td><td>${a.matched} / ${a.mismatched}</td><td>${a.percent === null ? "아직 측정하지 않음" : a.percent + "%"}</td></tr>`).join("")}</tbody></table></div>
    <p>직접 선택해 대조한 샘플만의 비율입니다. 읽지 못한 행과 아직 대조하지 않은 결과는 포함되지 않으며, 전체 객실 정확도를 뜻하지 않습니다. 같은 원문은 중복 집계하지 않고 앱·화면·규칙이 바뀌면 새로 집계합니다.</p>
    ${o?.channelReadings?.length ? `<button data-action="reading-check" data-id="${d.id}">방법별 결과 맞음·틀림 기록</button>` : ""}
    <p>Accessibility Insights를 별도로 실행할 필요는 없습니다. 글자 직접 읽기는 Windows의 UI Automation 기능을 사용합니다.</p>
    </div>`;
}
function detailPanel() {
  const d = state.devices.find((x) => x.id === selectedDevice);
  if (!d) return "";
  const o = d.batch?.observation;
  const staleImage =
    !d.online ||
    !d.captureFresh ||
    Date.now() - Date.parse(d.evidence?.captured_at) > 15000 ||
    (o?.errors?.length && !o.errors.every((e) => e.startsWith("UIA_")));
  return `<section class="panel"><div class="panel-head"><h2>호텔 ${escape(d.hotel_id)} · ${escape(d.machine)}</h2><button data-action="close-detail">닫기 ×</button></div><div class="detail">${readingStatus(d)}${methodsPanel(d)}<div class="actions">${state.capabilities?.includes("screen-reading-v2") ? `<button data-action="screen-settings" data-hotel="${escape(d.hotel_id)}">화면·객실 설정</button><button data-action="field-check" data-id="${d.id}">실제 문·키 동작 확인</button>` : ""}<button class="primary" data-action="analyze" data-id="${d.id}">지금 화면 분석</button><button data-action="profile" data-hotel="${escape(d.hotel_id)}">호텔별 규칙 편집</button><button data-action="label" data-hotel="${escape(d.hotel_id)}">확인한 판독 샘플 등록</button></div>${d.diagnosis.map((x) => `<div class="errorbanner"><b>${escape(x.title)}</b><br>${escape(x.action)}<p>${escape(x.detail)}</p></div>`).join("")}<div class="notice"><b>선택한 키텍 앱: ${escape(o?.selectedApp?.name || o?.windows?.[0]?.title || "앱 선택 대기")}</b><br>실행 프로그램: ${escape(o?.selectedApp?.process || o?.windows?.[0]?.process || "—")} · 화면 수집 ${ago(d.batch?.captured_at)}<br>이미지: ${d.evidence ? when(d.evidence.captured_at) : "수신 대기"} ${d.evidence ? badge(staleImage ? "마지막 저장 화면 · 현재 화면 확인 필요" : "최근 수집 화면", staleImage ? "amber" : "green") : ""}<br>화면 이미지는 최대 5초 간격, 텍스트는 기본 1.5초 간격으로 수집합니다.</div><p>업데이트: ${escape(d.update_status || "아직 보고되지 않음")} · 화면 관측 ${when(d.batch?.captured_at)}</p>${d.evidence ? `<img src="/api/evidence/${d.evidence.id}" alt="호텔 ${escape(d.hotel_id)}에서 전송한 RMS 수집 화면">` : '<div class="notice">전송된 화면 이미지가 없습니다. 기기에서 진단 화면 공유 여부를 확인해 주세요.</div>'}<h3>판독 원문과 실패 사유</h3><pre>${escape((o?.lines ?? []).map((l) => `${sourceName(l.source)} │ ${l.room || "—"}  ${l.code ? labels[l.code] || l.code : l.reason}  │ ${l.text}`).join("\n") || "판독 내용이 아직 없습니다.")}</pre><h3>Windows 환경</h3><pre>${escape(JSON.stringify({ os: o?.os, source: o?.source, ocrLanguage: o?.ocrLanguage, desktopAvailable: o?.desktopAvailable, remoteSession: o?.remoteSession, windows: o?.windows, regions: o?.regions }, null, 2))}</pre></div></section>`;
}
function updatesPanel() {
  return `<section class="panel"><div class="panel-head"><h2>앱 업데이트</h2>${badge("서명 · 무결성 검사", "green")}</div>${state.releases.length ? `<div class="table-scroll"><table><thead><tr><th>버전</th><th>상태</th><th>패키지</th><th>준비 시각</th></tr></thead><tbody>${state.releases.map((r) => `<tr><td><strong>${escape(r.version)}</strong></td><td>${badge(r.status === "active" ? "자동 업데이트 제공" : r.status, "green")}</td><td>${(r.size / 1024 / 1024).toFixed(1)} MB<small>SHA-256 ${r.sha256.slice(0, 20)}…</small></td><td>${when(r.created_at)}</td></tr>`).join("")}</tbody></table></div>` : empty("검증된 앱 패키지를 준비하고 있습니다", "배포되면 설치된 RmsLink가 자동으로 최신 버전을 확인합니다.", "↓")}<div class="notice">앱은 2분마다 업데이트를 확인합니다. Windows 트레이 메뉴의 ‘지금 업데이트 확인 · 적용’으로 수동 업데이트도 가능합니다. 새 앱 실행 확인에 실패하면 이전 버전으로 복구합니다.</div></section><section class="panel"><div class="panel-head"><h2>호텔별 판독 규칙</h2></div>${state.profiles.length ? `<div class="table-scroll"><table><thead><tr><th>호텔</th><th>규칙</th><th>상태</th><th>배포 시각</th><th></th></tr></thead><tbody>${state.profiles.map((p) => `<tr><td>${escape(p.hotel_id)}</td><td>${escape(p.body.name)} · r${p.revision}</td><td>${badge(p.status === "active" ? "현재 배포" : "이전 규칙", p.status === "active" ? "green" : "")}</td><td>${when(p.created_at)}</td><td>${p.status !== "active" ? `<button data-action="rollback" data-hotel="${escape(p.hotel_id)}" data-revision="${p.revision}">이 규칙으로 복구</button>` : ""}</td></tr>`).join("")}</tbody></table></div>` : empty("공통 키텍 규칙으로 시작합니다", "호텔별 차이가 확인되면 해당 호텔에만 새 규칙을 적용합니다.", "◇")}</section>`;
}
function render() {
  if (loginRequired) {
    root.innerHTML = `<main class="login"><section class="panel detail"><img src="/icon.svg" width="64" alt="RmsLink"><h1>RmsLink 대시보드</h1><p>접속 코드를 입력하면 호텔 현황과 실시간 수집 화면을 확인할 수 있습니다.</p><form id="login-form"><label for="access-code">대시보드 접속 코드</label><input id="access-code" type="password" autocomplete="current-password" required maxlength="128"><button class="primary">대시보드 접속</button></form>${err ? `<p role="alert">${escape(err)}</p>` : ""}<p class="sub">접속 코드는 관리 담당자에게 받을 수 있습니다. 로그인은 12시간 동안 유지됩니다.</p></section></main>`;
    return;
  }
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
    )}</div><div class="aside-bottom"><span class="dot"></span>호텔 통합 관제<br>키텍 연동 · 지속 개선 시스템<br>${remote ? '<button data-action="logout">로그아웃</button><br>' : ""}<span class="sub">키텍 실시간 연결</span></div></aside><main><header><span class="breadcrumb">워크스페이스 <strong>/ ${views[view]}</strong></span><span class="live"><span class="dot ${live ? "" : "warn"}"></span>${live ? "실시간 연결" : "다시 연결 중"}</span></header><div class="topline"><div><div class="eyebrow">KEYTECH INTEGRATION</div><h1>${view === "overview" ? "호텔 키텍 연동 현황" : views[view]}</h1><p class="sub">각 호텔의 연결부터 객실 상태, 개선과 업데이트까지 한곳에서 확인하세요.</p></div><div class="actions"><button data-action="guide">설정·분석 매뉴얼</button><button data-action="access">외부 접속 · 바로가기</button><button data-action="refresh">↻ 새로고침</button><button data-action="download" class="primary">↓ Windows 설치 파일</button></div></div>${err ? `<div class="errorbanner">${escape(err)}</div>` : ""}<div class="metrics">${[
    ["연결된 기기", online, `등록된 기기 ${devices.length}대`, "⌘"],
    [
      "키텍 수집 중",
      devices.filter((d) => d.status === "collecting").length,
      "수집 경로와 진단 정상",
      "⌁",
    ],
    ["확인이 필요한 기기", attention, "실패 원인과 조치 확인", "△"],
    ["관리 객실", state.rooms.length, "미확인 객실 포함 · 상태 근거 추적", "▦"],
  ]
    .map(
      ([label, n, foot, icon]) =>
        `<div class="metric"><div class="metric-top">${label}<span class="metric-icon">${icon}</span></div><div class="metric-value">${n}<small>${label === "관리 객실" ? "객실" : "대"}</small></div><div class="metric-foot">${foot}</div></div>`,
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
          `<p class="sub">설치 파일을 Windows PC로 옮겨 실행한 뒤 hotel_id를 입력하세요. 등록권은 발급 후 7일간, 최대 100대에 사용할 수 있습니다.</p><label>Windows용 다운로드 주소<input readonly id="download-link" value="${escape(link.url)}"></label><div class="actions"><button id="copy-link">주소 복사</button><a class="button primary" href="/api/installer">이 기기에 다운로드</a></div><div class="notice">현재 현장 시험본은 Windows 공인 코드 서명이 없습니다. 보안 차단이 나타나면 차단 내용을 확인해 주세요. 한국어 OCR이 없으면 앱에서 진단 원인이 표시됩니다.</div>`,
        );
        modal.querySelector("#copy-link").onclick = async () => {
          await navigator.clipboard.writeText(link.url);
          toast("설치 주소를 복사했습니다.");
        };
        break;
      case "guide":
        window.open("/manual.html", "_blank", "noopener");
        break;
      case "logout":
        await api("/api/logout", {});
        events?.close();
        loginRequired = true;
        state = null;
        err = "";
        render();
        break;
      case "access": {
        const access = remote
          ? { publicOrigin: state.access.publicOrigin }
          : await api("/api/access");
        openModal(
          "외부 접속 · 바탕화면 바로가기",
          `<p>어디에서 접속하든 같은 호텔 데이터가 표시됩니다.</p><label>대시보드 주소<input readonly value="${escape(access.publicOrigin)}/"></label>${access.code ? `<label>관리자 접속 코드<input type="password" id="remote-code" readonly value="${escape(access.code)}"></label><button id="show-code">접속 코드 표시</button>` : ""}<p class="sub">접속 코드는 운영 담당자에게만 전달하세요. 바로가기 파일에는 코드가 포함되지 않습니다.</p><a class="button primary" href="/api/shortcut">Windows 바탕화면 바로가기 다운로드</a><p class="sub">다운로드한 파일을 바탕화면으로 옮기세요.</p>`,
        );
        const show = modal.querySelector("#show-code");
        if (show)
          show.onclick = () => {
            modal.querySelector("#remote-code").type = "text";
            show.remove();
          };
        break;
      }
      case "analyze":
        const r = await api("/api/analyze", { deviceId: el.dataset.id });
        toast(
          r.queued ? "화면 분석을 요청했습니다." : r.reason || "분석 대기 중",
        );
        await refresh();
        break;
      case "screen-settings": {
        const hotel = el.dataset.hotel,
          p = nextProfile(hotel);
        openModal(
          "호텔 " + escape(hotel) + " 화면·객실 설정",
          `<p>RMS 화면에 실제로 표시되는 정보와 맞춰 주세요. 아이콘·색상만 표시되는 화면은 별도 판독 규칙이 필요합니다.</p>
          <label>표시되는 화면<select id="screen-mode"><option value="events" ${p.mode === "events" ? "selected" : ""}>발생 시각이 있는 이벤트 로그</option><option value="snapshot" ${p.mode === "snapshot" ? "selected" : ""}>객실별 현재 문·키 상태표</option></select></label>
          <label>실제 객실 번호 · 쉼표 또는 줄바꿈으로 구분<textarea id="screen-rooms" placeholder="101, 102, 103">${escape((p.expectedRooms ?? []).join(", "))}</textarea></label>
          <label>화면 갱신 시각 패턴 · 선택<input id="screen-clock" value="${escape(p.liveClockPattern ?? "")}" placeholder="관리 담당자가 화면의 갱신 시각을 검증한 경우에만 입력"></label>
          <p class="sub">현재 상태표는 값이 계속 같고 갱신 시각도 확인되지 않으면 60초 후 해당 상태를 미확인으로 표시합니다. 일반 PC 시계를 RMS 갱신 시각으로 등록하지 마세요.</p>`,
          async (m) => {
            const rooms = m
              .querySelector("#screen-rooms")
              .value.split(/[\s,]+/)
              .filter(Boolean);
            if (new Set(rooms).size !== rooms.length)
              throw Error("객실 번호가 중복되어 있습니다.");
            await api("/api/profiles", {
              ...p,
              mode: m.querySelector("#screen-mode").value,
              expectedRooms: rooms,
              liveClockPattern: m.querySelector("#screen-clock").value.trim(),
            });
            toast(
              "화면 설정을 저장했습니다. 기기가 새 규칙을 받으면 판독을 다시 확인합니다.",
            );
          },
        );
        break;
      }
      case "reading-check": {
        const d = state.devices.find((d) => d.id === el.dataset.id),
          batch = d?.batch;
        const choices = batch?.observation.channelReadings ?? [];
        if (!choices.length) {
          toast("새 화면의 읽기 결과를 기다려 주세요.");
          break;
        }
        openModal(
          "방법별 읽기 결과 대조",
          `<p>원문과 실제 객실 상태를 직접 확인하세요. 틀린 판독도 그대로 기록하며, 이 기록은 실제 객실 상태나 판독 규칙을 바꾸지 않습니다.</p>
          <label>대조할 결과<select id="reading-index">${choices.map((r, i) => `<option value="${i}">${sourceName(r.source)} · ${escape(r.room)}호 · ${escape(labels[r.code])} · ${escape(r.rawLine)}</option>`).join("")}</select></label>
          <label>직접 확인한 실제 객실 번호<input id="reading-room" maxlength="30" placeholder="예: 101"></label>
          <label>직접 확인한 실제 상태<select id="reading-code"><option value="">선택하세요</option>${Object.entries(
            labels,
          )
            .map(
              ([code, label]) =>
                `<option value="${code}">${escape(label)}</option>`,
            )
            .join("")}</select></label>
          <label class="check-label"><input id="reading-confirm" type="checkbox"> 실제 화면과 객실의 문·키 상태를 직접 대조했습니다.</label>`,
          async (m) => {
            if (!m.querySelector("#reading-confirm").checked)
              throw Error("실제 상태를 먼저 확인해 주세요.");
            await api("/api/reading-checks", {
              deviceId: d.id,
              batchId: batch.id,
              index: Number(m.querySelector("#reading-index").value),
              expectedRoom: m.querySelector("#reading-room").value.trim(),
              expectedCode: m.querySelector("#reading-code").value,
              confirmed: true,
            });
            toast("맞음·틀림을 해당 읽기 방법의 대조 기록에 저장했습니다.");
          },
        );
        break;
      }
      case "field-check": {
        const d = state.devices.find((d) => d.id === el.dataset.id),
          batch = d?.batch;
        const choices = (batch?.observation.readings ?? []).filter(
          (r) => Date.now() - Date.parse(r.occurredAt) < 120000,
        );
        if (!choices.length) {
          toast("실제 문·키 동작 후 최근 판독이 도착할 때까지 기다려 주세요.");
          break;
        }
        openModal(
          "실제 문·키 동작과 대조",
          `<p>한 시험 객실에서 문 열기·닫기, 손님 키 꽂기·빼기를 순서대로 실행하세요. 아래 값이 직접 확인한 동작과 일치할 때만 저장합니다.</p>
          <label>대조할 최근 판독<select id="field-reading">${choices.map((r, i) => `<option value="${i}">${escape(r.room)}호 · ${escape(labels[r.code])} · ${sourceName(r.source)} · ${escape(r.rawLine)}</option>`).join("")}</select></label>
          <label class="check-label"><input type="checkbox" id="field-confirm"> 실제 객실에서 수행한 동작과 화면·판독 결과가 일치함을 확인했습니다.</label>`,
          async (m) => {
            if (!m.querySelector("#field-confirm").checked)
              throw Error("실제 동작과 화면을 먼저 대조해 주세요.");
            const r = choices[Number(m.querySelector("#field-reading").value)];
            await api("/api/field-checks", {
              deviceId: d.id,
              batchId: batch.id,
              room: r.room,
              code: r.code,
              confirmed: true,
            });
            toast("현장 대조 결과를 저장했습니다.");
          },
        );
        break;
      }
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
          "화면 분석 근거",
          `<div class="detail"><pre>${escape(analysisText(JSON.stringify(j.result, null, 2)))}</pre></div>`,
        );
        break;
      }
    }
  } catch (e) {
    toast(e.message);
  }
});
root.addEventListener("submit", async (e) => {
  if (e.target.id !== "login-form") return;
  e.preventDefault();
  const password = document.querySelector("#access-code").value;
  try {
    await api("/api/login", { password });
    loginRequired = false;
    err = "";
    await refresh();
    connect();
  } catch (error) {
    err = error.message;
    render();
  }
});
function connect() {
  events?.close();
  events = new EventSource("/api/events");
  let pending;
  events.addEventListener("ready", () => {
    live = true;
    render();
  });
  events.addEventListener("change", () => {
    if (pending) return;
    pending = setTimeout(() => {
      pending = null;
      refresh();
    }, 300);
  });
  events.onerror = () => {
    live = false;
    render();
    refresh();
  };
  events.onopen = () => {
    live = true;
    render();
  };
}
try {
  const session = await api("/api/bootstrap", {});
  loginRequired = !!session.loginRequired;
  remote = !!session.remote;
  if (loginRequired) render();
  else {
    await refresh();
    connect();
  }
} catch (e) {
  err = e.message;
  render();
}
setInterval(() => {
  if (!modal && !loginRequired) refresh();
}, 5000);
