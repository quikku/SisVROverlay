@echo off
chcp 65001 > nul
rem Steam が管理者として動いていると、こちらも管理者でないと SteamVR に繋がらないため管理者で起動する
fltmc > nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
rem SteamVR 起動中に実行すると、SteamVR と一緒に自動起動するよう登録します
cd /d "%~dp0app"
GodanOverlay.exe --register --no-wait
pause
