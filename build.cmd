@echo off
rem ---------------------------------------------------------------------------
rem  DartScape development build.
rem
rem  Builds the solution and stages a ready-to-run instance into bin\.
rem
rem    build.cmd              Release build, runs the tests
rem    build.cmd -Debug       Debug build
rem    build.cmd -SkipTests   build only
rem    build.cmd -Clean       wipe bin\ build output (keeps the local world)
rem ---------------------------------------------------------------------------

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
endlocal
