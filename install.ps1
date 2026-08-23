# ============================================================
#  RmsLink 설치 스크립트 (관리자 권한 불필요, 사용자 폴더에 설치)
#  사용법: PowerShell 창에 이 파일 내용을 그대로 붙여넣기
#  - 붙여넣은 명령은 '다운로드한 파일'이 아니므로 SmartScreen 차단을 받지 않습니다.
#  - 다운로드된 실행파일의 다운로드표식(MOTW)은 Unblock-File 로 제거합니다.
# ============================================================

# [필수] 아래 DB 접속문자열을 실제 값으로 바꿔서 붙여넣으세요. (공개 바이너리에는 비밀이 없습니다)
$Conn = 'DB_CONNECTION_STRING_HERE'

$RepoUrl = 'https://github.com/yeonsooooooo/rms-link/releases/latest/download/RmsLink.zip'

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'RmsLink'
$app = Join-Path $dir 'app'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$log = Join-Path $dir ('install-{0}.log' -f (Get-Date -Format yyyyMMdd-HHmmss))
Start-Transcript -Path $log -Force | Out-Null

try {
    Write-Host '===== RmsLink 설치 시작 =====' -ForegroundColor Cyan
    Write-Host ("Windows: " + [Environment]::OSVersion.VersionString)

    $zip = Join-Path $env:TEMP 'RmsLink.zip'
    Write-Host "다운로드: $RepoUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $RepoUrl -OutFile $zip -UseBasicParsing
    Write-Host ("다운로드 완료: {0:N0} bytes" -f (Get-Item $zip).Length) -ForegroundColor Green

    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $app | Out-Null

    Write-Host '압축 해제...'
    Expand-Archive -Path $zip -DestinationPath $app -Force

    Write-Host '다운로드 표식(MOTW) 제거...'
    Get-ChildItem $app -Recurse | Unblock-File

    $exe = Join-Path $app 'RmsLink.exe'
    if (-not (Test-Path $exe)) {
        throw "실행 파일이 없습니다. 백신(Windows Defender)이 삭제했을 수 있습니다.`n   -> 관리자 PowerShell에서 다음 실행 후 재시도:`n   Add-MpPreference -ExclusionPath '$app'"
    }
    Write-Host "설치 위치: $exe" -ForegroundColor Green

    # DB 접속정보 기록 (이 스크립트에만 존재)
    $cfg = Join-Path $dir 'config.json'
    if ($Conn -and $Conn -ne 'DB_CONNECTION_STRING_HERE') {
        @{ ConnString = $Conn } | ConvertTo-Json | Set-Content -Path $cfg -Encoding UTF8
        Write-Host 'config.json 기록 완료 (DB 연동 활성).' -ForegroundColor Green
    } elseif (-not (Test-Path $cfg)) {
        Write-Host 'DB 접속문자열 미입력 → 로컬 백업만 됩니다.' -ForegroundColor Yellow
    }

    Write-Host '자체 점검(--selftest) 실행...'
    $p = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -WindowStyle Hidden
    Write-Host ("자체 점검 종료코드: {0}" -f $p.ExitCode)
    $report = Join-Path $dir 'selftest-report.txt'
    if (Test-Path $report) {
        Write-Host '--------- 자체 점검 리포트 ---------' -ForegroundColor Cyan
        Get-Content $report | ForEach-Object { Write-Host $_ }
        Write-Host '-----------------------------------' -ForegroundColor Cyan
    }
    if ($p.ExitCode -ne 0) {
        throw '자체 점검 실패(OCR 사용 불가 등). 위 리포트를 확인하세요. 대개 한국어 언어팩 설치로 해결됩니다.'
    }

    Write-Host '앱 실행...'
    Start-Process -FilePath $exe

    Write-Host ''
    Write-Host '===== 설치 완료 =====' -ForegroundColor Green
    Write-Host '트레이(화면 우측 하단 ^) 아이콘에서 최초 설정(호텔ID / 로그영역 드래그)을 진행하세요.'
}
catch {
    Write-Host ''
    Write-Host ('설치 실패: {0}' -f $_.Exception.Message) -ForegroundColor Red
}
finally {
    Stop-Transcript | Out-Null
    Write-Host ("전체 설치 로그: {0}" -f $log) -ForegroundColor Yellow
}
