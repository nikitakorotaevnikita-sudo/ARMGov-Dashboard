@echo off
rem ============================================================
rem  ARMGov Dashboard launcher (ASCII-only for cmd.exe safety)
rem  Starts the server, WAITS until it is ready, opens browser.
rem ============================================================
setlocal
cd /d "%~dp0"

set "EXE=dist\armgov-standalone.exe"
if not exist "%EXE%" set "EXE=bin\Release\net10.0\armgov-standalone.exe"

if not exist "%EXE%" (
  echo [!] Build not found.
  echo     Portable bundle:  dotnet publish -c Release -r win-x64 --self-contained true -o dist
  echo     Dev build:        dotnet build -c Release
  pause
  exit /b 1
)

rem --- Already running? Just open the browser ---
call :check
if not errorlevel 1 (
  echo Server already running. Opening http://localhost:5080/
  start "" http://localhost:5080/
  exit /b 0
)

echo Starting ARMGov Dashboard:  "%EXE%"
start "ARMGov Dashboard" "%EXE%"

echo Waiting for server to become ready (up to ~30s)...
for /l %%i in (1,1,30) do (
  ping -n 2 127.0.0.1 >nul
  call :check
  if not errorlevel 1 (
    echo Ready. Opening http://localhost:5080/
    start "" http://localhost:5080/
    exit /b 0
  )
)

echo [!] Server did not respond within ~30s.
echo     Check the "ARMGov Dashboard" window for the error message.
pause
exit /b 1

:check
rem errorlevel 0 = server responds, 1 = not yet
powershell -NoProfile -Command "try{[void](Invoke-WebRequest 'http://localhost:5080/' -UseBasicParsing -TimeoutSec 2);exit 0}catch{exit 1}"
exit /b %errorlevel%
