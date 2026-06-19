@echo off
chcp 65001 >nul
rem ============================================================
rem  ARMGov Dashboard - запуск на любой машине Windows
rem  Открывает http://localhost:5080/ и стартует сервер.
rem ============================================================
setlocal
cd /d "%~dp0"

set "EXE=dist\armgov-standalone.exe"
if not exist "%EXE%" set "EXE=bin\Release\net10.0\armgov-standalone.exe"

if not exist "%EXE%" (
  echo [!] Сборка не найдена.
  echo     Портативный бандл:  dotnet publish -c Release -r win-x64 --self-contained true -o dist
  echo     Для разработки:     dotnet build -c Release
  pause
  exit /b 1
)

echo Запуск ARMGov Dashboard:  "%EXE%"
echo Открываю http://localhost:5080/  (Ctrl+C - остановить сервер)
start "" http://localhost:5080/
"%EXE%"
