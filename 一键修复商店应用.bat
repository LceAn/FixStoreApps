@echo off
rem ================================================================
rem  One-click Windows Store app repair launcher
rem  Double-click me when a Store app fails to open / shows errors.
rem  Requires: FixStoreApps.ps1 in the same folder.
rem ================================================================
setlocal
cd /d "%~dp0"
if not exist "%~dp0FixStoreApps.ps1" (
    echo [ERROR] FixStoreApps.ps1 not found next to this file.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FixStoreApps.ps1" %*
endlocal
