@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -SourceDir "%~dp0"
if errorlevel 1 pause
