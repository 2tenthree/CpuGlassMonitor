@echo off
REM Run this file as Administrator (right click -> Run as administrator).
REM It points the logon task and both shortcuts at
REM   D:\SoftWare\CpuGlassMonitor\CpuGlassMonitor.exe
REM restarts the task, and writes a verification report next to this file.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix-autostart.ps1"
echo.
echo Done. Report: %~dp0fix-result.txt
pause
