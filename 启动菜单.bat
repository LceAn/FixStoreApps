@echo off
rem ================================================================
rem  Store App Repair Kit - launcher menu
rem  Double-click me. Requires Menu.ps1 in the same folder.
rem ================================================================
setlocal
cd /d "%~dp0"
if not exist "%~dp0Menu.ps1" (
    echo [ERROR] Menu.ps1 not found next to this file.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Menu.ps1" %*
endlocal
