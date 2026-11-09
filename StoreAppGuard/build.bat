@echo off
rem ================================================================
rem  Build StoreAppGuard.exe with the built-in .NET Framework C# compiler
rem  (no SDK required). Output: StoreAppGuard.exe next to this script.
rem ================================================================
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework csc.exe not found.
    exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /out:"%~dp0StoreAppGuard.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Xml.Linq.dll ^
  "%~dp0StoreAppGuard.cs"
if errorlevel 1 (
    echo [ERROR] build failed
    exit /b 1
)
echo [OK] built: %~dp0StoreAppGuard.exe
endlocal
