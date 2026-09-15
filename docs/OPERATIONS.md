[설치·설정·분석 매뉴얼](SETUP-ANALYSIS.md)

# RmsLink 운영

## 주소·데이터

- Mac mini 관제: http://127.0.0.1:18760
- Windows 전용 HTTPS: https://ys-macmini.tail984bfd.ts.net:8443
- 외부 수신기는 `127.0.0.1:18761`에만 바인딩하며 Tailscale Funnel 8443을 사용합니다. 기존 443 게이트웨이를 변경하지 않습니다.
- 데이터: `~/Library/Application Support/RmsLink` (0700)
- 기기 인증키는 Windows DPAPI CurrentUser로 보관합니다. Mac에는 SHA-256 해시만 저장합니다.
- `update-private.pem`은 Mac 밖으로 배포하지 않습니다. `update-public.pem`만 설치본에 넣습니다. 앱 업데이트의 RSA 서명은 Windows 공인 코드 서명과 별개입니다.
- 설치본에 들어간 등록권은 7일, 최대 100대입니다. 기기 등록 후에는 만료와 관계없이 기기 키로 연결합니다. 설치 주소와 EXE는 사내 담당자에게 전달합니다. 관리 토큰은 포함하지 않습니다.
- 일반 사용자 실행은 반드시 hotel_id를 입력합니다. 이전 호텔 outbox는 당시 hotel_id/session_id를 유지합니다.

## 서버 설치 및 복구

`node scripts/install-mac.mjs`는 로그인 시 시작되는 LaunchAgent를 설치합니다. Mac 재부팅 후 사용자 로그인이 필요합니다. 서버 종료·재시작 시 SQLite/WAL을 복구하며 분석 중이던 작업은 interrupted로 표시합니다.

```sh
launchctl print gui/$(id -u)/kr.co.rosegold.rmslink-control
```

서버 코드: `/Users/ys/dev/rms-link`의 `codex/rmslink-control` 브랜치. Windows 자동 배포는 이 브랜치의 `build.yml` 성공 실행을 추적합니다. 브랜치를 바꾸면 `RMSLINK_RELEASE_BRANCH`도 변경하세요.

## 계속 개선하기

1. Windows에서 실패 단계·선택 RMS 화면·원문·판독 실패 이유를 올립니다.
2. Mac은 동일 실패 중복을 6시간 억제하고 호텔별 자동 분석 사이에 30분을 둡니다. 하루 최대 4건, 동시 1건으로 Codex를 실행합니다. 대시보드에서 수동 요청도 가능합니다.
3. 기기 상세에서 **확인한 판독 샘플 등록**에 실제 키텍 조작과 대조한 원문/객실/상태를 기록합니다.
4. Codex가 제안한 새 어휘가 확인된 원문 및 상태와 일치하고 공통/호텔 회귀 검증에 통과하면 새 프로필이 자동 배포됩니다. 그 외에는 근거와 추가 조치를 대시보드에 남깁니다.
5. 화면 수집 API 또는 코드 자체를 바꿔야 할 때는 진단의 `needsCodeChange`와 `action`을 사용해 코드를 수정합니다. `RmsLink.csproj` 버전을 증가시켜 추적 브랜치에 push하면 Windows CI와 자동 배포가 이어집니다. 현재 자동 Codex 작업은 실행 가능한 C# 코드를 임의 생성·배포하지 않습니다.

Codex 실행은 로그인된 CLI를 사용하며 API 키를 호텔 PC에 전달하지 않습니다. 추론은 OpenAI 서비스에서 수행됩니다. `jobs/*/evidence.json`에는 선택 RMS 화면의 텍스트 진단을, `result.json`에는 구조화한 원인을 저장합니다. 화면의 지시문은 신뢰하지 않는 데이터로 취급합니다. 분석 실패·인증 만료·사용량 부족은 작업 실패로 표시합니다.

## 업데이트 배포

`build.yml`은 Windows에서 공통 파서 검증, 서버 회귀, 자체 포함 앱 빌드, DPAPI·OCR 런타임 로드 검사를 수행합니다. `validation.json`의 커밋·버전·패키지 해시를 Mac에서 재확인합니다. 같은 버전을 다른 바이너리로 덮어쓸 수 없습니다.

Windows는 RSA 서명된 안내의 만료·호텔 범위·패키지 크기·SHA-256을 확인합니다. ZIP 경로 이탈을 거부하고, 새 버전 자체 점검 후 별도 launcher가 버전을 전환합니다. 90초 내 정상 실행 표시가 없으면 이전 버전으로 돌아갑니다. 다운받은 파일이 검증됐다는 사실만으로 물리 키텍 동작 성공을 표시하지 않습니다.

새 설치 EXE 만들기: `scripts/package.py --artifacts <Windows-CI-산출물> --bootstrap <비공개-bootstrap.json> --output <설치.exe>`. 설치 관리자는 .NET 포함 실행 파일이며 PowerShell 실행 정책을 변경하지 않습니다.

## 보관·제한

- Windows outbox는 암호화하며 서버의 정확한 batch ID 확인 뒤 삭제합니다. 서버가 거부한 자료는 `rejected/`에 보존하고 상태에 건수를 표시합니다. outbox가 15,000개에 도달하면 새 수집을 대기하여 임의 유실을 방지합니다.
- 화면 증거는 최대 5초 간격으로 전송하며 24시간 후 제거합니다. 일반 진단은 14일, 이벤트는 90일 보관합니다. 분석 작업이 참조한 진단은 재현을 위해 보존합니다.
- 내부 관제는 loopback Host/Origin 검증 후 자동 로그인합니다. 외부 HTTPS 관제는 `dashboard-access.key`의 별도 256비트 접속 코드와 Secure/HttpOnly/SameSite 쿠키를 사용하며 세션은 12시간 후 만료됩니다. 기기 키와 기존 내부 admin 토큰으로 외부 관제에 로그인할 수 없습니다. 역할별 다중 사용자 계정은 제공하지 않습니다.
- 외부 로그인 실패는 15분에 12회로 제한합니다. 코드를 재발급하려면 서버를 중지하고 `dashboard-access.key`를 별도로 보관한 뒤 파일을 제거하고 서버를 재시작하세요. 기존 세션도 서버 재시작 시 만료됩니다. `node scripts/desktop-mac.mjs`를 다시 실행해 개인 접속 안내 파일을 갱신합니다.
- 대시보드는 Windows 화면 수집 이미지를 5초 간격으로 받습니다. 저장량과 네트워크 사용을 고려하고, 불필요하면 Windows의 이미지 공유 체크를 해제하세요.
- 자동 창 탐색이 지원하지 않는 제품명, 캡처 가림, UIA 공급자 비호환, 한국어 OCR 품질은 현장 검증이 필요합니다. 사라진 창이나 읽지 못한 상태를 정상으로 만들지 않습니다.

공식 참고: [Windows 데스크톱 WinRT API](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-apis-desktop-apps), [Codex 비대화형 실행](https://learn.chatgpt.com/docs/non-interactive-mode), [Tailscale Funnel](https://tailscale.com/docs/reference/tailscale-cli/funnel).
