@echo off
rem ================================================================
rem  Reset all Microsoft Store (AppX) package registrations
rem  = the user's original remedy command, wrapped with progress UI
rem  Requires: ResetStoreIndex.ps1 in the same folder.
rem ================================================================
setlocal
cd /d "%~dp0"
if not exist "%~dp0ResetStoreIndex.ps1" (
    echo [ERROR] ResetStoreIndex.ps1 not found next to this file.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0ResetStoreIndex.ps1" %*
endlocal
