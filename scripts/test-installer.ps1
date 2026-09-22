$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'RmsLink'
$appData = Join-Path $env:APPDATA 'RmsLink'
$version = ([xml](Get-Content RmsLink.csproj)).Project.PropertyGroup.Version
New-Item -ItemType Directory -Path '.work' -Force | Out-Null
# Fresh-user collection must work without current.txt, an EXE, or configuration.
powershell.exe -NoProfile -File ./collect-logs.ps1 -OutputDirectory "$pwd/.work/support-before-install"
if ($LASTEXITCODE -ne 0 -or -not (Get-ChildItem '.work/support-before-install/*.zip')) { throw 'Pre-install diagnostic bundle missing' }
@{serverUrl='https://example.invalid'; enrollmentCode=('a'*48); updatePublicKey='CI fixture only'} | ConvertTo-Json | Set-Content '.work/ci-bootstrap.json'
python scripts/package.py --artifacts "$pwd" --bootstrap "$pwd/.work/ci-bootstrap.json" --output "$pwd/.work/RmsLink-Setup.exe" --dotnet dotnet
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed' }
function Run-Installer([string]$Exe,[string]$Report,[int]$Expected) {
    $p=Start-Process -FilePath $Exe -ArgumentList @('--install-test',('"'+$Report+'"')) -PassThru
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw 'Installer timed out' }
    if ($p.ExitCode -ne $Expected -or -not (Test-Path $Report)) { throw "Unexpected installer exit: $($p.ExitCode)" }
    return Get-Content $Report -Raw | ConvertFrom-Json
}
$result=Run-Installer "$pwd/.work/RmsLink-Setup.exe" "$pwd/installer-fixture.json" 0
if (-not $result.passed -or -not $result.runtimeVerified -or $result.version -ne $version) { throw 'Installer runtime check failed' }
# Exercise the normal installer UI path, including its child-process startup handshake.
$gui=Start-Process -FilePath "$pwd/.work/RmsLink-Setup.exe" -PassThru
if(-not $gui.WaitForExit(180000)){$gui.Kill();throw 'Normal installer UI timed out'}
if($gui.ExitCode -ne 0){throw 'Normal installer UI failed'}
$guiResult=Get-Content "$root/installation-status.json" -Raw | ConvertFrom-Json
if($guiResult.stage -ne 'COMPLETE' -or $guiResult.status -ne 'passed'){throw 'Normal installer did not reach setup readiness'}
$guiApp=Get-Process RmsLink -ErrorAction Stop | Where-Object {$_.Path -eq (Join-Path $root "versions/$version/RmsLink.exe").Replace('/','\')} | Select-Object -First 1
if(-not $guiApp -or $guiApp.MainWindowHandle -eq 0){throw 'Normal installer did not leave the setup window open'}
$guiApp.CloseMainWindow() | Out-Null
if(-not $guiApp.WaitForExit(5000)){$guiApp.Kill();$guiApp.WaitForExit()}
# Actual GUI startup handshake, including the setup form constructor and Shown event.
$ready="$root/startup-ci.json"
$exe=Join-Path $root "versions/$version/RmsLink.exe"
$p=Start-Process $exe -ArgumentList @('--setup','--startup-report',('"'+$ready+'"')) -PassThru
try {
    for($i=0;$i -lt 100 -and -not (Test-Path $ready);$i++){ Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready)) { throw 'Setup window startup handshake failed' }
} finally { if(-not $p.HasExited){$p.Kill();$p.WaitForExit()} }
# Preserve an existing setup and automatic-start preference across reinstall.
New-Item -ItemType Directory -Path $appData -Force | Out-Null
'broken configuration deliberately retained' | Set-Content (Join-Path $appData 'config.json')
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RmsLink -ErrorAction SilentlyContinue
$null=Run-Installer "$pwd/.work/RmsLink-Setup.exe" "$pwd/installer-reinstall.json" 0
if ((Get-Content (Join-Path $appData 'config.json') -Raw) -notmatch 'deliberately retained') { throw 'Reinstall overwrote configuration' }
if ($null -ne [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run','RmsLink',$null)) { throw 'Reinstall reset automatic-start preference' }
# A tiny corrupt payload exercises the real failed runtime path, without disturbing the good version.
$bad="$pwd/.work/bad-payload"
New-Item -ItemType Directory -Path "$bad/versions/99.0.0/assets" -Force | Out-Null
Copy-Item '.work/ci-bootstrap.json' "$bad/bootstrap.json"
'{"version":"99.0.0"}' | Set-Content "$bad/install.json"
'not an executable' | Set-Content "$bad/versions/99.0.0/RmsLink.exe"
'fixture' | Set-Content "$bad/RmsLinkLauncher.exe"
Copy-Item 'assets/dashboard.ico' "$bad/versions/99.0.0/assets/dashboard.ico"
Compress-Archive "$bad/*" '.work/bad-payload.zip'
dotnet publish installer/Installer.csproj -c Release -r win-x64 --self-contained true "-p:PayloadPath=$pwd/.work/bad-payload.zip" -o .work/bad-setup
if ($LASTEXITCODE -ne 0) { throw 'Failure fixture build failed' }
$failed=Run-Installer "$pwd/.work/bad-setup/RmsLinkLauncher.exe" "$pwd/installer-failure.json" 1
if ($failed.passed -or $failed.stage -ne 'RUNTIME') { throw 'Failure stage not reported' }
if ((Get-Content "$root/current.txt" -Raw).Trim() -ne $version) { throw 'Failed installation changed the working version' }
powershell.exe -NoProfile -File ./collect-logs.ps1 -OutputDirectory "$pwd/.work/support-after-failure"
if ($LASTEXITCODE -ne 0) { throw 'PowerShell 5.1 diagnostic collection failed' }
$bundle=Get-ChildItem '.work/support-after-failure/*.zip' | Select-Object -First 1
Expand-Archive $bundle.FullName '.work/support-inspection'
if (-not (Test-Path '.work/support-inspection/install-installation-status.json')) { throw 'Installer failure missing from diagnostics' }
if (-not (Test-Path '.work/support-inspection/environment.json')) { throw 'Broken config prevented collection' }
Remove-Item (Join-Path $appData 'config.json')
@{passed=$true;version=$version;install=$true;setupWindow=$true;normalInstallerUI=$true;reinstallPreservesSettings=$true;failedRuntimePreservesVersion=$true;standaloneDiagnostics=$true;physicalKeytechVerified=$false} | ConvertTo-Json | Set-Content 'installer-fixture-validation.json'
