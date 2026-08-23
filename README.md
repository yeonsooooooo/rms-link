# RmsLink — RMS 화면 연동 에이전트

호텔 PC의 RMS(객실관리 프로그램) 화면에서 **문열림/문닫힘/키삽입/키제거/외출** 등 이벤트를
Windows 내장 OCR로 읽어 Neon(Postgres)로 전송하는 상주 프로그램. 외부 유료 API 없음.

- 설치형 단일 폴더(자체 포함, .NET 설치 불필요)
- 지정한 화면 영역만 캡처 → 변화 있을 때만 OCR (저사양 대응)
- 트레이 상주, 판독 미리보기 창으로 실시간 정확도 확인
- DB 접속정보는 **공개 바이너리에 없음**. 설치 스크립트가 로컬 config.json에만 기록

---

## 설치 (크롬 원격 접속 상태 권장)

관리자 권한 불필요. 대상 PC에서:

1. **시작 → `powershell` 입력 → PowerShell 실행** (관리자 아니어도 됨)
2. `install.ps1` 내용을 복사해 창에 **붙여넣고 Enter**
   - 맨 위 `$Conn = '...'` 을 실제 DB 접속문자열로 바꿔서 붙여넣기
   - 붙여넣은 명령은 파일이 아니므로 **SmartScreen 차단이 없습니다**
3. 트레이(우측 하단 `^`)의 RmsLink 아이콘 → 최초 설정(호텔ID / 로그영역 드래그)

설치 스크립트는 전 과정을 `%LOCALAPPDATA%\RmsLink\install-*.log` 에 기록하고,
`--selftest`(OCR/DB/화면 자가진단) 결과까지 출력합니다.

### 설치가 안 될 때
- **"실행 파일이 없습니다"(백신 삭제)** → 관리자 PowerShell에서
  `Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\RmsLink\app"` 실행 후 재설치
- **자체 점검 실패(OCR 불가)** → [설정 > 시간 및 언어 > 언어]에서 **한국어** 추가(언어팩)

---

## 문제 발생 시 로그 수집

원격에서 원인을 보려면 로그가 필요합니다. 둘 중 하나:

- 앱이 떠 있으면: 트레이 아이콘 → **"진단 로그 바탕화면으로 내보내기"**
- 앱이 안 뜨면: PowerShell에 `collect-logs.ps1` 붙여넣기 → 바탕화면에 zip 생성

zip에는 앱로그/설치로그/이벤트백업/자가진단/시스템정보가 담기며 **DB 비밀번호는 마스킹**됩니다.

---

## 동작 원리

1. 지정 화면 영역 캡처(GDI, 다중 모니터/원격세션 지원)
2. 직전 프레임과 해시 비교 → 변화 없으면 OCR 생략
3. Windows.Media.Ocr(한국어)로 줄 단위 인식
4. `EventParser`가 객실번호 + 이벤트어휘 + 시각 파싱 (1글자 오독 허용)
5. `rms_events` 테이블에 UPSERT(중복 차단), 로컬 jsonl 백업, 60초마다 heartbeat

## DB 스키마 (Neon)

- `rms_events(hotel_id, room, event_code, event_raw, event_time, event_date, raw_line, captured_at)`
- `rms_agent_status(hotel_id, last_seen, version, machine, ocr_lang, events_total)`

## 로컬 빌드 (참고)

```
dotnet publish RmsLink.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=true -o publish
```

CI(GitHub Actions)가 windows-latest에서 자동 빌드 후 Release에 `RmsLink.zip`을 첨부합니다.
