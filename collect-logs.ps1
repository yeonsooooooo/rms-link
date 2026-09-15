$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'RmsLink'
$version = (Get-Content (Join-Path $root 'current.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '설치 버전을 확인할 수 없습니다.' }
$exe = Join-Path $root "versions\$version\RmsLink.exe"
Start-Process $exe -ArgumentList '--collectlogs' -Wait
