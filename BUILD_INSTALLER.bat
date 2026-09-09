@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo Inno Setup 6 bulunamadi.
  echo Paket ureten bilgisayara Inno Setup 6 kurun.
  if /i not "%~1"=="--no-pause" pause
  exit /b 1
)
call "%~dp0PUBLISH_WIN_X64.bat" --no-pause
if errorlevel 1 (
  echo Publish basarisiz. Setup olusturulmadi.
  if /i not "%~1"=="--no-pause" pause
  exit /b 1
)
"%ISCC%" /Qp "installer\NSXVeriKurtarmaPro.iss"
set "RESULT=%ERRORLEVEL%"
if "%RESULT%"=="0" echo Setup hazir: %CD%\SETUP
if not "%RESULT%"=="0" echo HATA: Setup derlenemedi.
if /i not "%~1"=="--no-pause" pause
exit /b %RESULT%
