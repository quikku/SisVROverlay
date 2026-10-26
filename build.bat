@echo off
chcp 65001 > nul
rem ソースを変更したらこれで app フォルダを作り直す
cd /d "%~dp0src"
dotnet publish -c Release -o ..\app
pause
