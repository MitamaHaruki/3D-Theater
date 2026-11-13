@echo off
rem Collect 3D Theater logs and PC information into a zip on the desktop (see collect-logs.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-logs.ps1"
echo.
pause
