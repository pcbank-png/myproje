@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
echo NSX Veri Kurtarma Pro - Bir sonraki surumun guncelleme paketi
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GUNCELLEME_PAKETI_OLUSTUR.ps1"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo HATA: Guncelleme paketi olusturulamadi. Yukaridaki hatayi kontrol edin.
if /i not "%~1"=="--no-pause" pause
exit /b %RESULT%
