# RmsLink · 키텍 관측과 지속 개선

Windows 호텔 PC의 키텍/RMS 화면을 읽어 Mac mini에 전달하고, 원인 분석·호텔별 판독 규칙·검증된 앱 업데이트를 관리합니다. **실제 장치 연동 성공은 현장에서 문/키를 조작하고 수신 결과를 대조해야 확인됩니다.**

## Windows 설치

1. Mac mini의 [RmsLink 관제](http://127.0.0.1:18760) → **Windows 설치 파일**에서 설치 EXE 또는 다운로드 주소를 받습니다.
2. Windows x64에서 `RmsLink-Setup.exe`를 실행합니다. .NET과 개발 도구를 따로 설치하지 않습니다. 현재 배포본은 공인 Authenticode 서명이 없는 현장 시험본입니다. Windows 정책에서 차단되면 차단 사유를 확인해야 하며, 프로그램은 보안 설정을 해제하지 않습니다.
3. **실행할 때마다 hotel_id를 직접 입력합니다.** 앱 업데이트에 따른 내부 재시작과 영역 재지정은 같은 호텔 세션을 이어갑니다.
4. 키텍/RMS 창이 하나이면 자동으로 탐색합니다. 그렇지 않으면 **로그 영역 직접 지정**을 선택합니다. 한국어 Windows OCR 언어팩이 없으면 진단 원인을 보고합니다.
5. 트레이의 **판독 미리보기**, **지금 업데이트 확인 · 적용**, **로그 영역 다시 지정**, **호텔 ID 변경**으로 운영합니다.

Windows 10 1809 이상, x64를 대상으로 합니다. Windows 로그인 이후 동작하는 화면 수집 앱입니다. 잠금·로그아웃·RMS 최소화·가려진 화면은 정상 판독을 보장하지 않습니다. Windows 서비스로 Session 0에서 화면을 캡처하지 않습니다.

## 구성

- **Windows:** RMS 후보 창 탐색 → UI Automation 데이터 행 → Windows OCR → 엄격한 이벤트 판독 → DPAPI 암호화 outbox → HTTPS. RMS/도어락의 문을 여는 제어 명령은 없습니다.
- **Mac mini:** 기기 인증, SQLite 이벤트/진단 보존, 상태 신선도 계산, SSE 실시간 대시보드, Codex 진단 큐.
- **호텔 프로필:** `hotel_id`별 어휘·객실 번호·판독 방식·OCR 배율·주기. 공통 규칙과 별개로 서명된 프로필을 배포합니다.
- **개선:** 실패 관측을 Codex CLI에 전달합니다. 확인된 현장 샘플과 동일 C# 파서의 회귀 검증을 통과한 어휘 수정은 해당 호텔에 자동 배포합니다. 확인되지 않은 의미는 `needs_evidence`, 코드 수정이 필요한 경우는 구체적인 조치와 근거를 남깁니다. LLM 추측만으로 문·키 상태를 확정하지 않습니다.
- **앱 업데이트:** 코드 수정 + 버전 증가 + 추적 브랜치 push → Windows CI 빌드·실행 검증 → Mac mini가 5분마다 검증 산출물 확인 → RSA 서명 안내 → Windows가 2분마다 자동 적용. 트레이 메뉴에서 수동 확인도 가능합니다. 새 버전 실행 확인 실패 시 이전 버전으로 복구하고 실패 버전 재설치를 보류합니다.

## 상태의 의미

`door`와 `key`는 별도로 갱신합니다. `snapshot`은 현재 화면에서 읽은 값, `event`는 마지막 발생 이벤트로 추정한 값입니다. 현재 화면은 60초, 이벤트는 5분, 기기 연결은 45초가 기준입니다. 오래됐거나 수집 오류가 있으면 `unknown/stale`로 표시합니다. 객실 입실/청소중/공실만으로 키 삽입을 추론하지 않습니다. 늦게 재전송된 이벤트가 최신 상태를 덮어쓰지 않습니다.

**첫 설치 시 모든 객실의 현재 키 상태가 자동으로 확보되는 것은 아닙니다.** RMS에 현재 상태표가 있으면 호텔 프로필을 `snapshot`으로 맞추고 현장 샘플을 검증하세요. 과거 이벤트 로그만 보이는 환경에서는 새로운 키/문 이벤트를 기다려야 합니다. 화면 외의 제조사 통신 프로토콜, 직접 DB, USB/시리얼 드라이버는 이번 수집기의 연결 방식이 아닙니다.

## 개발 및 검증

Node 24+, .NET 8 SDK가 필요합니다. Windows 바이너리는 macOS에서도 교차 컴파일하며 실행 검증은 Windows CI에서 합니다.

```sh
npm ci
dotnet run --project tests/core/Core.csproj -c Release
dotnet publish tests/core/Core.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o .work/core
RMSLINK_CORE="$PWD/.work/core/RmsLinkCore" npm test
dotnet build RmsLink.csproj -c Release
RMSLINK_CORE="$PWD/.work/core/RmsLinkCore" npm start
```

- [구조와 운영](docs/OPERATIONS.md)
- [현장 설치 후 확인](docs/FIELD-CHECK.md)
- [Windows 검증 워크플로](.github/workflows/build.yml)

Aside의 원본 코드를 복제하지 않고, 로컬 실행기·관측·진단 기록·실시간 구독 구조를 적용했습니다. 이전 Windows→Neon 직접 DB 전송과 DB 비밀번호 배포는 제거했습니다. 원래 서버에 연결하는 DB 자격증명은 설치 파일에 포함하지 않습니다.
