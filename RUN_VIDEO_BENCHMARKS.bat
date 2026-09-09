@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PROJECT=tests\NSXVeriKurtarmaPro.VideoBenchmarks\NSXVeriKurtarmaPro.VideoBenchmarks.csproj"
set "OUTPUT=%~dp0BENCHMARK_RESULTS"
set "CORPUS=%~dp0tests\VideoCorpus"
set "MANIFEST=%CORPUS%\video-corpus.json"
set "BASELINE=%CORPUS%\video-benchmark-baseline.json"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK bulunamadi.
  exit /b 1
)

if "%~1"=="" goto default_run
if /i "%~1"=="--update-baseline" goto update_baseline
if exist "%~f1\" goto directory_run
if exist "%~f1" goto file_run
goto custom_run

:default_run
dotnet run --project "%PROJECT%" -c Release -- --self-test --corpus "%CORPUS%" --manifest "%MANIFEST%" --baseline "%BASELINE%" --require-baseline-completeness --output "%OUTPUT%"
goto finish

:update_baseline
dotnet run --project "%PROJECT%" -c Release -- --self-test --corpus "%CORPUS%" --manifest "%MANIFEST%" --baseline "%BASELINE%" --require-complete-corpus --require-baseline-completeness --write-baseline "%BASELINE%" --output "%OUTPUT%"
goto finish

:directory_run
dotnet run --project "%PROJECT%" -c Release -- --self-test --corpus "%~f1" --baseline "%BASELINE%" --output "%OUTPUT%"
goto finish

:file_run
dotnet run --project "%PROJECT%" -c Release -- --self-test --reference "%~f1" --baseline "%BASELINE%" --output "%OUTPUT%"
goto finish

:custom_run
dotnet run --project "%PROJECT%" -c Release -- --self-test --corpus "%CORPUS%" --manifest "%MANIFEST%" --baseline "%BASELINE%" --require-baseline-completeness --output "%OUTPUT%" %*

:finish
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" exit /b %EXIT_CODE%
echo Benchmark tamamlandi: %OUTPUT%
exit /b 0
