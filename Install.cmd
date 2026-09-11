@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1"
if errorlevel 1 echo Installation failed. See QUICKSTART.md or run Diagnose.cmd.
pause
