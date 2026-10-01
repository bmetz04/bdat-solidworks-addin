@echo off
REM Double-click to update BDAT on this computer. The work is done in update.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1"
