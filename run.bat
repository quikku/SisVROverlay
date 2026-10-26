@echo off
chcp 65001 > nul
rem Steam が管理者として動いていると、こちらも管理者でないと SteamVR に繋がらないため管理者で起動する
fltmc > nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0app"
GodanOverlay.exe
pause
