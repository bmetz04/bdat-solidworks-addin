@echo off
REM Builds BDAT.dll with the C# compiler that ships with Windows (no Visual Studio or .NET SDK needed).
REM The code must stay C# 5 compatible for this to work.
REM Optional first argument: the SolidWorks api\redist folder.
REM Waits for a key at the end only when double-clicked (no arguments, started by cmd /c, BDAT_NO_PAUSE not set),
REM so the window doesn't vanish before you can read it. Scripts set BDAT_NO_PAUSE=1 or pass the api folder.
setlocal
set PAUSE_AT_END=
if "%~1"=="" if not "%BDAT_NO_PAUSE%"=="1" echo %CMDCMDLINE% | find /i "/c" >nul && set PAUSE_AT_END=1
set API=%~1
if "%API%"=="" set API=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set SRC=%~dp0src\BDAT
set OUT=%SRC%\bin\Release\net48
if not exist "%OUT%" mkdir "%OUT%"

"%CSC%" /nologo /target:library /platform:x64 /optimize+ /out:"%OUT%\BDAT.dll" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%API%\SolidWorks.Interop.sldworks.dll" /r:"%API%\SolidWorks.Interop.swconst.dll" /r:"%API%\SolidWorks.Interop.swpublished.dll" ^
  /recurse:"%SRC%\*.cs" "%~dp0build\AssemblyInfo.cs"
if errorlevel 1 (
  echo Build failed. If it says the file is in use, close SolidWorks and try again.
  if "%PAUSE_AT_END%"=="1" pause
  exit /b 1
)
copy /y "%API%\SolidWorks.Interop.*.dll" "%OUT%\" >nul
echo Built %OUT%\BDAT.dll
if "%PAUSE_AT_END%"=="1" pause
