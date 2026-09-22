@echo off
powershell.exe -NoProfile -File "%~dp0collect-logs.ps1"
if errorlevel 1 echo Diagnostic collection failed. Keep this window and record its error message.
pause
