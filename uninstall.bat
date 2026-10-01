@echo off
REM Removes BDAT from SolidWorks. Right-click > Run as administrator.
set DLL=%~dp0src\BDAT\bin\Release\net48\BDAT.dll
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /unregister "%DLL%"
pause
