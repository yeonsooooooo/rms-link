@echo off
powershell.exe -NoProfile -File "%~dp0collect-logs.ps1"
if not errorlevel 1 goto success
call :fallback
if errorlevel 1 goto failed
:success
if /I not "%~1"=="--no-pause" pause
exit /b 0
:failed
echo Diagnostic collection failed. Record this window's error message.
if /I not "%~1"=="--no-pause" pause
exit /b 1
:fallback
rem A restrictive PowerShell policy must not prevent collecting installer failures.
rem This path uses only file copies; it does not change or bypass that policy.
set "RMSLINK_SUPPORT_DIR=%TEMP%\RmsLink-support-%RANDOM%-%RANDOM%"
mkdir "%RMSLINK_SUPPORT_DIR%" || exit /b 1
for %%F in (installation-status.json launcher-status.json install-runtime.json failed-update.json current.txt installer.log) do if exist "%LOCALAPPDATA%\RmsLink\%%F" copy /y "%LOCALAPPDATA%\RmsLink\%%F" "%RMSLINK_SUPPORT_DIR%\install-%%F" >nul
for %%F in (connection-status.json capture-status.json selftest-report.json) do if exist "%APPDATA%\RmsLink\%%F" copy /y "%APPDATA%\RmsLink\%%F" "%RMSLINK_SUPPORT_DIR%\%%F" >nul
for %%F in (installation-status.json launcher-status.json) do if exist "%TEMP%\RmsLink-%%F" copy /y "%TEMP%\RmsLink-%%F" "%RMSLINK_SUPPORT_DIR%\fallback-%%F" >nul
(ver & echo Collection mode: command-shell fallback. PowerShell collection could not run. & echo Check Windows security messages if no installation status exists.) > "%RMSLINK_SUPPORT_DIR%\environment.txt"
echo This fallback excludes settings, credentials, screenshots, and queues. > "%RMSLINK_SUPPORT_DIR%\READ-ME.txt"
where tar.exe >nul 2>&1
if errorlevel 1 goto folder
tar.exe -a -c -f "%RMSLINK_SUPPORT_DIR%.zip" -C "%RMSLINK_SUPPORT_DIR%" .
if errorlevel 1 goto folder
echo Diagnostic ZIP saved: %RMSLINK_SUPPORT_DIR%.zip
exit /b 0
:folder
echo Diagnostic folder saved: %RMSLINK_SUPPORT_DIR%
exit /b 0
