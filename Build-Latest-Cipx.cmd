@echo off
setlocal
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Build-Latest-Cipx.ps1"
if errorlevel 1 pause
endlocal
