@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo   NSX NATIVE MOBILE - ROTATE HARD ROUTE FIX
echo ============================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0APPLY_NSX_NATIVE_ROTATE_HARD_ROUTE.ps1"
if errorlevel 1 (
  echo.
  echo [HATA] Fix uygulanamadi. Yukaridaki ilk hata satirini kontrol edin.
  pause
  exit /b 1
)

echo.
echo [OK] Program.cs guncellendi.
echo Sonraki adim: PUBLISH_NSX_WEB_PLESK_NATIVE_MOBILE_SAGLAM.bat
pause
exit /b 0
