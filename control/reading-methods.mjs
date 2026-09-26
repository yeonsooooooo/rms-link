// Describe observed behavior without claiming a program can never expose UIA text.
export function readingMethods(o) {
  const u = o?.methods?.uia,
    c = o?.methods?.ocr;
  if (!u || !c)
    return {
      title: "읽기 방식 상세 확인 대기",
      detail:
        "이전 버전의 자료에는 직접 읽기를 못 한 이유가 없습니다. 최신 앱에서 새 화면을 받아 주세요.",
    };
  let title, detail;
  if (u.status === "skipped_region") {
    title = "선택한 부분을 사진으로 읽음";
    detail =
      "화면 일부만 선택해 OCR을 사용했습니다. 직접 읽기는 검사하지 않았으므로 가능 여부를 아직 모릅니다.";
  } else if (u.status === "not_attempted") {
    title = "글자 읽기 시작 전";
    detail = "앱 선택·실행·화면 잠금 상태를 확인해 주세요.";
  } else if (["timeout", "error", "stale"].includes(u.status)) {
    title = "직접 읽기 확인 실패 · OCR 사용 시도";
    detail =
      u.status === "timeout"
        ? "프로그램이 제시간에 응답하지 않았습니다. 영구적으로 읽을 수 없다는 뜻은 아닙니다."
        : u.status === "stale"
          ? "직접 읽기 응답이 늦어 이번 화면에 사용하지 않았습니다."
          : "프로그램의 직접 읽기 요청이 실패했습니다. 실행 권한과 프로그램 상태를 확인해 주세요.";
  } else if (u.lineCount === 0) {
    title = "직접 가져온 글자 없음 · OCR 사용 시도";
    detail =
      "이번 화면에서 직접 가져올 글자가 없어서 사진의 글자를 읽는 OCR을 사용했습니다. 프로그램 전체의 지원 여부를 확정한 것은 아닙니다.";
  } else if (u.candidateCount === 0) {
    title = "글자는 가져왔지만 객실 상태 해석 필요";
    detail =
      "메뉴나 제목 등은 읽었지만 객실·문·키 상태로 바꾸지 못했습니다. 화면 종류·객실 목록·상태 표현을 확인해 주세요.";
  } else {
    const supplemented = (o.readings ?? []).some((r) => r.source === "ocr");
    title = supplemented
      ? "직접 읽기 + 부족한 부분은 OCR"
      : "프로그램에서 글자를 직접 가져옴";
    detail = supplemented
      ? "직접 읽은 결과를 사용하고, 빠진 상태는 OCR로 두 번 연속 확인해 보충했습니다."
      : "객실 상태로 해석할 글자를 직접 가져왔습니다. OCR도 함께 시도해 결과를 대조합니다.";
  }
  const ocrDetail = {
    not_attempted: "OCR 실행 전 · 화면 수집 상태를 확인하세요",
    unavailable: "OCR 기능 없음 · Windows OCR 언어팩이 필요합니다",
    error: "OCR 처리 실패",
    empty: "OCR에서도 글자를 찾지 못함",
    ok: `사진에서 글자 ${c.lineCount}줄을 읽음`,
  }[c.status];
  let progress;
  const accepted = o.readings?.length ?? u.acceptedCount + c.acceptedCount;
  if (!accepted) {
    progress =
      o.coverage?.suggestedMode === "snapshot" && o.coverage?.mode === "events"
        ? "시각 없는 문·키 상태를 읽었습니다. 화면·객실 설정에서 현재 상태표인지 확인하세요."
        : o.coverage?.pending > 0
          ? "아직 반영한 상태가 없습니다. OCR을 다음 화면과 대조 중입니다. 계속 대기하면 글자 크기와 로그 영역을 확인하세요."
          : "아직 반영한 문·키 상태가 없습니다. 원문의 객실 번호·발생 시각·상태 문구와 실패 사유를 확인하세요.";
  } else if (
    o.readings?.every(
      (r) =>
        r.kind === "event" &&
        Date.parse(o.capturedAt) - Date.parse(r.occurredAt) >= 300000,
    )
  ) {
    progress =
      "과거 이벤트만 읽었습니다. 시험 객실에서 문·키를 조작하고 새 이벤트가 수신되는지 확인하세요.";
  } else {
    progress = `이번 화면에서 문·키 상태 ${accepted}개를 채택했습니다. 실제 동작과 대조해 주세요.`;
  }
  return { title, detail, ocrDetail, progress, uia: u, ocr: c };
}
