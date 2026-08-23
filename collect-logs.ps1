# ============================================================
#  RmsLink 진단 로그 수집 (설치가 안 되거나 동작이 이상할 때)
#  PowerShell 창에 그대로 붙여넣기 → 바탕화면에 zip 생성
# ============================================================
$ErrorActionPreference = 'SilentlyContinue'
$dir = Join-Path $env:LOCALAPPDATA 'RmsLink'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) ('RmsLink-진단-{0}.zip' -f (Get-Date -Format yyyyMMdd-HHmmss))

if (-not (Test-Path $dir)) {
    Write-Host "설치 폴더가 없습니다: $dir  (설치가 시작조차 안 된 상태일 수 있습니다)" -ForegroundColor Red
    # TEMP의 crash 로그라도 수집
    $crash = Get-ChildItem $env:TEMP -Filter 'RmsLink-crash-*.log' -ErrorAction SilentlyContinue
    if ($crash) { Compress-Archive -Path $crash.FullName -DestinationPath $out -Force; Write-Host "TEMP crash 로그 수집: $out" }
    return
}

$tmp = Join-Path $env:TEMP ('rmslink-diag-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Copy-Item (Join-Path $dir '*') $tmp -Recurse -Force -Exclude 'app'

# config.json 비밀번호 마스킹
$cfg = Join-Path $tmp 'config.json'
if (Test-Path $cfg) {
    (Get-Content $cfg -Raw) -replace '(?i)(Password=)[^;"\\]+', '$1***' | Set-Content $cfg -Encoding UTF8
}

# 시스템 정보 추가
$info = @()
$info += "수집 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$info += "머신: $env:COMPUTERNAME"
$info += "OS: $([Environment]::OSVersion.VersionString)"
$info += "설치 exe 존재: $(Test-Path (Join-Path $dir 'app\RmsLink.exe'))"
$info | Set-Content (Join-Path $tmp 'system-info.txt') -Encoding UTF8

Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $out -Force
Remove-Item $tmp -Recurse -Force
Write-Host "진단 파일 생성: $out" -ForegroundColor Green
Start-Process explorer.exe "/select,`"$out`""
