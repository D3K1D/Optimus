@echo off
rem Starts Optimus with the administrator actions, the firewall action and elevated scripts switched on.
rem For testing only. A normal start from the Optimus shortcut has them off again.
setlocal
set "DIR=%ProgramFiles%\Optimus"
if not exist "%DIR%\OptimizationTool.Host.exe" (
  echo Optimus is not installed in "%DIR%".
  pause
  exit /b 1
)
rem End a background part of Optimus that is still running from a normal start, so the switches take effect.
taskkill /im OptimizationTool.exe >nul 2>&1
taskkill /f /im OptimizationTool.Host.exe >nul 2>&1
timeout /t 2 /nobreak >nul
rem Developer log on: every part of Optimus writes %LOCALAPPDATA%\OptimizationTool\logs\dev.log while this file exists.
if not exist "%LOCALAPPDATA%\OptimizationTool" mkdir "%LOCALAPPDATA%\OptimizationTool"
type nul > "%LOCALAPPDATA%\OptimizationTool\dev-log.on"
powershell -NoProfile -Command "Start-Process -WindowStyle Hidden -FilePath '%DIR%\OptimizationTool.Host.exe' -ArgumentList '--enable-admin-actions','--enable-elevated-scripts','--enable-firewall-handler','--idle-exit-seconds','600','--dev-log'"
timeout /t 3 /nobreak >nul
start "" "%DIR%\OptimizationTool.exe"
