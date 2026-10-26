@echo off
chcp 65001 > nul
rem Steam が管理者として動いていると、こちらも管理者でないと SteamVR に繋がらないため管理者で起動する
fltmc > nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
rem SteamVR の自動起動の登録を外します（SteamVR 起動中に実行）
cd /d "%~dp0app"
GodanOverlay.exe --unregister --no-wait
pause
