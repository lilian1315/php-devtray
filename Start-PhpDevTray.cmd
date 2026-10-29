@echo off
if not exist "%~dp0artifacts\releases\0.7.0\PhpDevTray.exe" (
  echo Build the application first: powershell -File scripts\build.ps1 -Publish
  exit /b 1
)
start "" "%~dp0artifacts\releases\0.7.0\PhpDevTray.exe"
