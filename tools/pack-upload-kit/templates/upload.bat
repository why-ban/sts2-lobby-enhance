@echo off
chcp 65001 >nul
title Game Lobby Enhance - Workshop Upload
cd /d "%~dp0"

set "PS=powershell"
where powershell >nul 2>nul || set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload.ps1"

rem upload.ps1 always pauses and exits 0 on its own. Reaching this branch means
rem PowerShell itself could not run, so keep the window open and say something useful.
if errorlevel 1 (
  echo.
  echo ============================================================
  echo  [ERROR] PowerShell did not finish.
  echo  Please take a screenshot of this whole window and send it
  echo  back to the author.
  echo ============================================================
  echo.
  pause
)
