@echo off
REM Registers BDAT with SolidWorks. Right-click > Run as administrator.
REM Close SolidWorks before running this.
set DLL=%~dp0src\BDAT\bin\Release\net48\BDAT.dll
if not exist "%DLL%" (
  echo Can't find %DLL%
  echo Build it first:  dotnet build src\BDAT\BDAT.csproj -c Release
  pause
  exit /b 1
)
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe" /codebase "%DLL%"
echo.
echo Done. Start SolidWorks and look for the BDAT tab/toolbar.
pause
