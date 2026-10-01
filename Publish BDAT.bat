@echo off
REM Double-click to publish a new BDAT build to the team. The work is done in publish.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
