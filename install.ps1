# Compatibility entry point. A missing installer is a failure, never a silent success.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'RmsLink-Setup.exe'
if (Test-Path -LiteralPath $installer -PathType Leaf) {
    $process = Start-Process -FilePath $installer -Wait -PassThru
    exit $process.ExitCode
}
Write-Error 'RmsLink-Setup.exe is required. Download it from https://rms-link.vercel.app/ and run it on the hotel PC. For failed installations, use collect-logs.cmd from the diagnostic ZIP.'
exit 1
