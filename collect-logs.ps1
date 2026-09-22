# Run with Windows PowerShell 5.1+. The RmsLink application need not be installed or runnable.
param([string]$OutputDirectory = [Environment]::GetFolderPath('Desktop'))
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'RmsLink'
$appRoot = Join-Path $env:APPDATA 'RmsLink'
$work = Join-Path $env:TEMP ('RmsLink-diagnostics-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$issues = New-Object System.Collections.Generic.List[string]
function Copy-Diagnostic([string]$Source, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return }
    try {
        $value = Get-Content -LiteralPath $Source -Raw
        if ($value.Length -gt 500000) { $value = $value.Substring($value.Length - 500000) }
        $value = $value -replace '\b[a-fA-F0-9]{48,64}\b', '[credential-or-digest-redacted]'
        Set-Content -LiteralPath (Join-Path $work $Name) -Value $value -Encoding UTF8
    } catch { $issues.Add($Name + ': ' + $_.Exception.GetType().Name) }
}
try {
    foreach ($name in @('installer.log','installation-status.json','launcher-status.json','install-runtime.json','failed-update.json','current.txt')) { Copy-Diagnostic (Join-Path $installRoot $name) ('install-' + $name) }
    foreach ($name in @('connection-status.json','capture-status.json','selftest-report.json')) { Copy-Diagnostic (Join-Path $appRoot $name) $name }
    foreach ($name in @('installation-status.json','launcher-status.json')) { Copy-Diagnostic (Join-Path $env:TEMP ('RmsLink-' + $name)) ('fallback-' + $name) }
    if (Test-Path -LiteralPath $appRoot) {
        Get-ChildItem -LiteralPath $appRoot -Filter '*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 7 | ForEach-Object { Copy-Diagnostic $_.FullName ('app-' + $_.Name) }
    }
    Get-ChildItem -LiteralPath $env:TEMP -Filter 'RmsLink-crash-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Copy-Diagnostic $_.FullName ('temp-' + $_.Name) }
    $server = $null
    foreach ($source in @((Join-Path $installRoot 'bootstrap.json'),(Join-Path $appRoot 'config.json'))) {
        if (-not (Test-Path -LiteralPath $source)) { continue }
        try {
            $cfg = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
            if ($cfg.serverUrl) { $server = $cfg.serverUrl }
            if ($source -like '*config.json') {
                $cfg | Select-Object hotelId,deviceId,serverUrl,autoUpdate,shareEvidence,selectedApp,regions | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $work 'settings-summary.json') -Encoding UTF8
            }
        } catch { $issues.Add('Unreadable configuration: ' + $_.Exception.GetType().Name) }
    }
    $network = 'No configured server; installer may not have started.'
    if ($server) {
        try {
            $uri = [Uri]$server
            if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') { throw 'Invalid server origin' }
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $health = Invoke-RestMethod -Uri ($server.TrimEnd('/') + '/health') -TimeoutSec 15 -MaximumRedirection 0
            $network = if ($health.ok -eq $true -and $health.service -eq 'RmsLink') { 'HTTPS service identity verified. Enrollment is a separate check.' } else { 'Unexpected service response' }
        } catch { $network = 'HTTPS check failed: ' + $_.Exception.GetType().Name + ' / ' + $_.Exception.Message }
    }
    [ordered]@{ at=(Get-Date).ToUniversalTime().ToString('o'); os=[Environment]::OSVersion.VersionString; is64Bit=[Environment]::Is64BitOperatingSystem; powershell=$PSVersionTable.PSVersion.ToString(); installed=(Test-Path (Join-Path $installRoot 'current.txt')); https=$network; issues=@($issues.ToArray()) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'environment.json') -Encoding UTF8
    'Check install-installation-status.json, connection-status.json and capture-status.json in order. No installer report can mean execution was blocked before startup. This tool excludes credentials, outbox data and screenshots.' | Set-Content (Join-Path $work 'READ-ME.txt') -Encoding UTF8
    if (-not $OutputDirectory) { $OutputDirectory = $env:TEMP }
    try { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null } catch { $OutputDirectory = $env:TEMP }
    $zip = Join-Path $OutputDirectory ('RmsLink-diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6) + '.zip')
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip
    Write-Host "Diagnostic file saved: $zip"
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
