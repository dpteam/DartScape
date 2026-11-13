@echo off
rem ---------------------------------------------------------------------------
rem  DartScape firewall helper.
rem
rem  Opens the game, status site, admin panel and relay ports on Windows
rem  Firewall. Elevates automatically.
rem
rem    firewall.cmd              game + web + relay open, admin on the LAN only
rem    firewall.cmd -WebAny      status site reachable from anywhere
rem                              (needed if the reverse proxy is elsewhere)
rem    firewall.cmd -Remove      delete the DartScape rules
rem ---------------------------------------------------------------------------

setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0firewall.ps1" %*
endlocal
