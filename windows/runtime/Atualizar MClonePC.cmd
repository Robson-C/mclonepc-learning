@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0updater\MClonePC-Updater.ps1"
set "UPDATE_EXIT=%ERRORLEVEL%"
if not "%UPDATE_EXIT%"=="0" pause
exit /b %UPDATE_EXIT%

