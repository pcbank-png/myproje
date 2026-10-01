@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================
rem NSX YAZILIM - PLESK / IIS .NET 8 FRAMEWORK-DEPENDENT PUBLISH
rem Existing live server update package.
rem Keeps live appsettings, App_Data, uploads, updates and logs.
rem ============================================================

set "PROJECT=%CD%\NSYazilim.Web.csproj"
set "WEB_CONFIG=%CD%\web.config"
set "WORK_ROOT=%CD%\.nsx-publish-work"
set "FULL_DIR=%WORK_ROOT%\FULL"
set "UPLOAD_DIR=%WORK_ROOT%\UPLOAD"
set "TEMP_ZIP=%WORK_ROOT%\NSX_Web_Plesk_DLL.zip"
set "ZIP_FILE=%CD%\NSX_Web_Plesk_DLL.zip"
set "NSX_ZIP=%TEMP_ZIP%"
set "NSX_UPLOAD=%UPLOAD_DIR%"
set "NSX_FULL=%FULL_DIR%"

echo ============================================================
echo   NSX YAZILIM - PLESK DLL PUBLISH
echo   Target : .NET 8 / Framework Dependent
echo   Start  : dotnet NSYazilim.Web.dll
echo ============================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 goto :DOTNET_MISSING
where powershell >nul 2>&1
if errorlevel 1 goto :POWERSHELL_MISSING
where robocopy >nul 2>&1
if errorlevel 1 goto :ROBOCOPY_MISSING

set "DOTNET_MAJOR="
for /f "tokens=1 delims=." %%M in ('dotnet --version 2^>nul') do set "DOTNET_MAJOR=%%M"
if not defined DOTNET_MAJOR goto :DOTNET_MISSING
if !DOTNET_MAJOR! LSS 8 goto :DOTNET_OLD

if not exist "%PROJECT%" goto :PROJECT_MISSING
if not exist "%WEB_CONFIG%" goto :WEB_CONFIG_MISSING

rem Critical cloud source modules used by the live site.
if not exist "%CD%\SalonTakipCloud\SalonTakipCloudEndpointExtensions.cs" goto :CLOUD_MISSING
if not exist "%CD%\TeknikServisCloud\TeknikServisCloudEndpointExtensions.cs" goto :CLOUD_MISSING
if not exist "%CD%\VeresiyeFreeCloud\VeresiyeFreeCloudEndpointExtensions.cs" goto :CLOUD_MISSING
if not exist "%CD%\CaritakipCloud\CaritakipCloudEndpointExtensions.cs" goto :CLOUD_MISSING

rem Native mobile backend must be present and wired into CaritakipCloud before publishing.
if not exist "%CD%\CaritakipCloud\CaritakipNativeMobileEndpointExtensions.cs" goto :NATIVE_MOBILE_MISSING
if not exist "%CD%\CaritakipCloud\Services\CaritakipNativeMobileStore.cs" goto :NATIVE_MOBILE_MISSING
findstr /I /C:"MapNsxCaritakipNativeMobile" "%CD%\CaritakipCloud\CaritakipCloudEndpointExtensions.cs" >nul || goto :NATIVE_MOBILE_MISSING
findstr /I /C:"CaritakipNativeMobileStore" "%CD%\CaritakipCloud\CaritakipCloudEndpointExtensions.cs" >nul || goto :NATIVE_MOBILE_MISSING

rem Never leave a stale successful package beside the BAT when a new run fails.
rem Delete the previous delivery only after all preflight checks above passed.
if exist "%ZIP_FILE%" del /f /q "%ZIP_FILE%"
if exist "%ZIP_FILE%" goto :STALE_OUTPUT_DELETE_FAILED

rem Clean only the fixed temporary work folder next to this BAT.
rem The final deliverable is one ZIP; FULL and UPLOAD never remain as outputs.
if /I not "%WORK_ROOT%"=="%CD%\.nsx-publish-work" goto :WORK_PATH_INVALID
if exist "%WORK_ROOT%" rmdir /s /q "%WORK_ROOT%"
if exist "%WORK_ROOT%" goto :CLEAN_FAILED
mkdir "%FULL_DIR%" || goto :FAILED
mkdir "%UPLOAD_DIR%" || goto :FAILED

echo [1/6] NuGet restore...
dotnet restore "%PROJECT%"
if errorlevel 1 goto :FAILED

echo.
echo [2/6] Release publish...
dotnet publish "%PROJECT%" ^
  -c Release ^
  -f net8.0 ^
  --no-restore ^
  --self-contained false ^
  -p:SelfContained=false ^
  -p:UseAppHost=false ^
  -p:PublishSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:PublishReadyToRun=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "%FULL_DIR%"
if errorlevel 1 goto :FAILED

rem Use the repository web.config intentionally.
copy /y "%WEB_CONFIG%" "%FULL_DIR%\web.config" >nul
if errorlevel 1 goto :FAILED

echo.
echo [3/6] Deployment identity...
rem Generate an immutable deployment identity from the exact published DLL.
rem This file is shipped with the package and exposed by /api/nsx-deploy after upload.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$ErrorActionPreference='Stop';" ^
 "$dll=Join-Path $env:NSX_FULL 'NSYazilim.Web.dll';" ^
 "$hash=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant();" ^
 "$utc=[DateTime]::UtcNow;" ^
 "$buildId=('nsx-'+$utc.ToString('yyyyMMdd-HHmmss')+'-'+$hash.Substring(0,12));" ^
 "$obj=[ordered]@{buildId=$buildId;publishedAtUtc=$utc.ToString('o');assemblySha256=$hash};" ^
 "$json=$obj|ConvertTo-Json -Compress;" ^
 "$utf8=New-Object System.Text.UTF8Encoding($false);" ^
 "[IO.File]::WriteAllText((Join-Path $env:NSX_FULL 'nsx-deploy.json'),$json,$utf8);" ^
 "Write-Host ('Build ID: '+$buildId)"
if errorlevel 1 goto :DEPLOY_MARKER_FAILED

rem Hard publish checks.
if not exist "%FULL_DIR%\NSYazilim.Web.dll" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\NSYazilim.Web.deps.json" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\NSYazilim.Web.runtimeconfig.json" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\web.config" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\nsx-deploy.json" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\nsx-assets\logo-brand.webp" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\salontakip\manifest.webmanifest" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\salontakip\sw.js" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\index.html" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\manifest.webmanifest" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\sw.js" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\app-icon.svg" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\app.css" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\app.js" goto :PUBLISH_INVALID
if not exist "%FULL_DIR%\wwwroot\caritakip\offline.html" goto :PUBLISH_INVALID
rem Soft asset checks: require caritakip shell files and a versioned CSS/JS reference.
rem Do NOT hardcode cache-bust tokens like 20260914-final20 here; those change often and
rem falsely fail an otherwise valid publish.
findstr /I /C:"caritakip/app.css?v=" "%FULL_DIR%\wwwroot\caritakip\index.html" >nul || goto :PUBLISH_INVALID
findstr /I /C:"caritakip/app.js?v=" "%FULL_DIR%\wwwroot\caritakip\index.html" >nul || goto :PUBLISH_INVALID
findstr /I /C:"nsx-cari-shell-" "%FULL_DIR%\wwwroot\caritakip\sw.js" >nul || goto :PUBLISH_INVALID

rem SQLite native runtime is required by project dependencies.
if exist "%FULL_DIR%\runtimes\win-x64\native\e_sqlite3.dll" goto :SQLITE_OK
if exist "%FULL_DIR%\e_sqlite3.dll" goto :SQLITE_OK
goto :SQLITE_MISSING

:SQLITE_OK
echo.
echo [4/6] Safe live-server upload folder...
rem Exclude by directory NAME (robocopy /XD). Absolute paths are unreliable across Windows builds.
rem Live secrets/data are also deleted below as defense in depth.
robocopy "%FULL_DIR%" "%UPLOAD_DIR%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np ^
  /XD App_Data logs uploads updates ^
  /XF appsettings.json appsettings.Development.json appsettings.Production.json appsettings.Production.example.json launchSettings.json >nul
set "RC=!ERRORLEVEL!"
if !RC! GEQ 8 goto :ROBOCOPY_FAILED

rem Defense in depth: remove secrets/data if a future project change publishes them.
if exist "%UPLOAD_DIR%\appsettings.json" del /f /q "%UPLOAD_DIR%\appsettings.json"
if exist "%UPLOAD_DIR%\appsettings.Development.json" del /f /q "%UPLOAD_DIR%\appsettings.Development.json"
if exist "%UPLOAD_DIR%\appsettings.Production.json" del /f /q "%UPLOAD_DIR%\appsettings.Production.json"
if exist "%UPLOAD_DIR%\appsettings.Production.example.json" del /f /q "%UPLOAD_DIR%\appsettings.Production.example.json"
if exist "%UPLOAD_DIR%\App_Data" rmdir /s /q "%UPLOAD_DIR%\App_Data"
if exist "%UPLOAD_DIR%\logs" rmdir /s /q "%UPLOAD_DIR%\logs"
if exist "%UPLOAD_DIR%\wwwroot\uploads" rmdir /s /q "%UPLOAD_DIR%\wwwroot\uploads"
if exist "%UPLOAD_DIR%\wwwroot\updates" rmdir /s /q "%UPLOAD_DIR%\wwwroot\updates"

echo.
echo [5/6] ZIP package...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
 "if(Test-Path -LiteralPath $env:NSX_ZIP){Remove-Item -LiteralPath $env:NSX_ZIP -Force};" ^
 "[IO.Compression.ZipFile]::CreateFromDirectory($env:NSX_UPLOAD,$env:NSX_ZIP,[IO.Compression.CompressionLevel]::Optimal,$false)"
if errorlevel 1 goto :FAILED
if not exist "%TEMP_ZIP%" goto :FAILED

echo.
echo [6/6] ZIP structure and safety checks...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
 "$z=[IO.Compression.ZipFile]::OpenRead($env:NSX_ZIP);" ^
 "try{" ^
 "$n=@($z.Entries|ForEach-Object{$_.FullName.Replace([char]92,[char]47)});" ^
 "$required=@('NSYazilim.Web.dll','NSYazilim.Web.deps.json','NSYazilim.Web.runtimeconfig.json','web.config','nsx-deploy.json','wwwroot/nsx-assets/logo-brand.webp','wwwroot/salontakip/manifest.webmanifest','wwwroot/salontakip/sw.js','wwwroot/caritakip/index.html','wwwroot/caritakip/manifest.webmanifest','wwwroot/caritakip/sw.js','wwwroot/caritakip/app-icon.svg','wwwroot/caritakip/app.css','wwwroot/caritakip/app.js','wwwroot/caritakip/offline.html');" ^
 "foreach($r in $required){if($n -notcontains $r){throw ('ZIP missing: '+$r)}};" ^
 "if(@($n|Where-Object{$_ -match '(^|/)appsettings([^/]*)[.]json$'}).Count -gt 0){throw 'ZIP contains appsettings JSON'};" ^
 "if(@($n|Where-Object{$_ -match '(^|/)(App_Data|logs)(/|$)'}).Count -gt 0){throw 'ZIP contains live data/log folder'};" ^
 "if(@($n|Where-Object{$_ -match '^wwwroot/(uploads|updates)(/|$)'}).Count -gt 0){throw 'ZIP contains live uploads/updates'};" ^
 "if(@($n|Where-Object{$_ -match '[.]pdb$'}).Count -gt 0){throw 'ZIP contains debug symbols'};" ^
 "$sqlite=@($n|Where-Object{$_ -match '(^|/)e_sqlite3[.]dll$'}); if($sqlite.Count -eq 0){throw 'ZIP missing e_sqlite3.dll'};" ^
 "Write-Host ('ZIP files: '+$z.Entries.Count)" ^
 "}finally{$z.Dispose()}"
if errorlevel 1 goto :FAILED

rem Promote the already validated archive as the only delivery output.
move /y "%TEMP_ZIP%" "%ZIP_FILE%" >nul
if errorlevel 1 goto :FINALIZE_FAILED
if not exist "%ZIP_FILE%" goto :FINALIZE_FAILED

rem Remove all intermediate publish folders. Only the ZIP remains beside the BAT.
if /I not "%WORK_ROOT%"=="%CD%\.nsx-publish-work" goto :WORK_PATH_INVALID
if exist "%WORK_ROOT%" rmdir /s /q "%WORK_ROOT%"
if exist "%WORK_ROOT%" goto :WORK_CLEANUP_FAILED

echo.
echo ============================================================
echo   PUBLISH SUCCESS
echo ============================================================
echo ZIP:
echo %ZIP_FILE%
echo Only this ZIP is the delivery output. No FULL/UPLOAD folder remains.
echo.
echo IMPORTANT FOR PLESK:
echo - Do NOT delete the live site folder before upload.
echo - Keep live appsettings.json.
echo - Keep App_Data, wwwroot\uploads and wwwroot\updates.
echo - Server must have ASP.NET Core .NET 8 Hosting Bundle.
echo - For unattended translation queues, enable Always Running / disable IIS idle timeout in Plesk.
echo - After upload, open: https://www.nsxyazilim.com/api/nsx-deploy
echo - The returned buildId must match the nsx-deploy.json inside this ZIP.
echo - Any normal page response also carries the same X-NSX-Build header.
echo ============================================================
if exist "%SystemRoot%\explorer.exe" start "" explorer.exe /select,"%ZIP_FILE%"
pause
exit /b 0

:DOTNET_MISSING
echo [ERROR] .NET SDK was not found on this development computer.
goto :END_ERROR

:DOTNET_OLD
echo [ERROR] .NET SDK 8 or newer is required. Current major: !DOTNET_MAJOR!
goto :END_ERROR

:POWERSHELL_MISSING
echo [ERROR] Windows PowerShell was not found on this development computer.
goto :END_ERROR

:ROBOCOPY_MISSING
echo [ERROR] Robocopy was not found on this development computer.
goto :END_ERROR

:PROJECT_MISSING
echo [ERROR] NSYazilim.Web.csproj was not found next to this BAT.
goto :END_ERROR

:WEB_CONFIG_MISSING
echo [ERROR] web.config was not found next to this BAT.
goto :END_ERROR

:CLOUD_MISSING
echo [ERROR] A required cloud module is missing.
echo Required: SalonTakipCloud, TeknikServisCloud, VeresiyeFreeCloud, CaritakipCloud.
goto :END_ERROR

:NATIVE_MOBILE_MISSING
echo [ERROR] Native mobile backend files or wiring are missing.
echo Required:
echo - CaritakipCloud\CaritakipNativeMobileEndpointExtensions.cs
echo - CaritakipCloud\Services\CaritakipNativeMobileStore.cs
echo - CaritakipCloudEndpointExtensions.cs must register/map native mobile support.
goto :END_ERROR


:STALE_OUTPUT_DELETE_FAILED
echo [ERROR] Previous NSX_Web_Plesk_DLL.zip could not be removed.
echo Close Explorer/ZIP tools using it and retry.
goto :END_ERROR

:CLEAN_FAILED
echo [ERROR] Temporary .nsx-publish-work folder could not be removed.
echo Close any Explorer/editor window using that folder and retry.
goto :END_ERROR

:WORK_PATH_INVALID
echo [ERROR] Temporary publish path safety check failed.
goto :END_ERROR

:FINALIZE_FAILED
echo [ERROR] Validated ZIP could not be moved next to the BAT.
goto :END_ERROR

:WORK_CLEANUP_FAILED
echo [ERROR] ZIP was created but temporary FULL/UPLOAD folders could not be removed.
goto :END_ERROR

:PUBLISH_INVALID
echo [ERROR] Publish output is incomplete.
echo Required DLL/deps/runtimeconfig/web.config/static files were not produced.
echo Also verify wwwroot\caritakip index.html references app.css/app.js with ?v= and sw.js has nsx-cari-shell-.
goto :END_ERROR

:SQLITE_MISSING
echo [ERROR] Windows e_sqlite3.dll was not produced by publish.
echo The package is not accepted because SQLite may fail on Plesk.
goto :END_ERROR

:ROBOCOPY_FAILED
echo [ERROR] Safe upload folder copy failed. Robocopy code: !RC!
goto :END_ERROR

:DEPLOY_MARKER_FAILED
echo [ERROR] nsx-deploy.json could not be generated from the published DLL.
goto :END_ERROR

:FAILED
echo [ERROR] Publish/package operation failed.
goto :END_ERROR

:END_ERROR
if /I "%WORK_ROOT%"=="%CD%\.nsx-publish-work" if exist "%WORK_ROOT%" rmdir /s /q "%WORK_ROOT%"
echo.
echo ============================================================
echo   PUBLISH FAILED
echo   Check the first ERROR line above.
echo ============================================================
pause
exit /b 1
