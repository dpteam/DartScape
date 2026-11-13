@echo off
rem ---------------------------------------------------------------------------
rem  DartScape launcher script.
rem
rem    run.cmd             start the game server (bin\)
rem    run.cmd client      start the client directly
rem    run.cmd launcher    start the launcher
rem    run.cmd status      show the status page URL
rem
rem  Run build.cmd first.
rem ---------------------------------------------------------------------------

setlocal
set "BIN=%~dp0bin"

if not exist "%BIN%\DartScape.Server.exe" (
  echo bin\DartScape.Server.exe not found.
  echo Run build.cmd first.
  exit /b 1
)

if /i "%~1"=="client" (
  shift
  cd /d "%BIN%"
  DartScape.Client.exe %*
  exit /b %ERRORLEVEL%
)

if /i "%~1"=="launcher" (
  shift
  cd /d "%BIN%"
  DartScape.Launcher.exe %*
  exit /b %ERRORLEVEL%
)

if /i "%~1"=="status" (
  echo   public : http://localhost:43595/
  echo   admin  : http://localhost:43596/   ^(LAN only^)
  echo   game   : localhost:43594
  exit /b 0
)

cd /d "%BIN%"
DartScape.Server.exe %*
endlocal
