@echo off
setlocal enabledelayedexpansion

:: ============================================
::   Omega Studio — release publisher
:: ============================================
::

echo.
echo ============================================
echo     Omega Studio - release publisher
echo ============================================

set "SOLUTION=OmegaStudio.slnx"
set "PROJECT=OmegaStudio\OmegaStudio.csproj"
set "PUBLISH_DIR=publish"
set "ERROR_COUNT=0"
set "START_TIME=%TIME: =0%"

cd /d "%~dp0"

if exist "%PUBLISH_DIR%" (
    echo [INFO] Cleaning previous publish output...
    rmdir /s /q "%PUBLISH_DIR%"
)

echo.
echo [INFO] Building all projects in Release...
dotnet build "%SOLUTION%" -c Release --nologo --verbosity minimal
if errorlevel 1 (
    echo.
    echo [ERROR] Release build failed. Nothing was published.
    goto :Finish
)

call :Publish "win-x64"     "false"
call :Publish "linux-x64"   "true"
call :Publish "linux-arm64" "true"

:Finish
echo.
echo ============================================
if "%ERROR_COUNT%"=="0" (
    echo   PUBLISH COMPLETED SUCCESSFULLY
    echo   Output: %~dp0%PUBLISH_DIR%
    echo ============================================
    echo.
    for /d %%D in ("%PUBLISH_DIR%\*") do (
        echo   %%~nxD
        for %%F in ("%%D\*") do echo     %%~nxF  %%~zF bytes
        if exist "%%D\formats" (
            for %%F in ("%%D\formats\*.dll") do echo     formats\%%~nxF  %%~zF bytes
        ) else (
            echo     [WARN] no formats folder - this build opens nothing
        )
    )
) else (
    echo   PUBLISH COMPLETED WITH ERRORS
    echo   Failed targets: %ERROR_COUNT%
    echo ============================================
)

call :Elapsed "%START_TIME%" "%TIME: =0%"
echo.
echo [INFO] Elapsed: !ELAPSED!
echo.
pause
exit /b %ERROR_COUNT%

:Publish
set "RUNTIME=%~1"
set "SELF_CONTAINED=%~2"
set "OUTPUT=%PUBLISH_DIR%\%~1"

echo.
echo ============================================
echo   Publishing %RUNTIME%  (self-contained: %SELF_CONTAINED%)
echo ============================================

set "EXTRA="
if /i "%SELF_CONTAINED%"=="true" set "EXTRA=-p:EnableCompressionInSingleFile=true"

dotnet publish "%PROJECT%" -c Release -r %RUNTIME% ^
  --self-contained %SELF_CONTAINED% ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none ^
  -p:DebugSymbols=false ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  %EXTRA% ^
  -o "%OUTPUT%" ^
  --nologo ^
  --verbosity minimal

if errorlevel 1 (
    echo [ERROR] %RUNTIME% failed.
    set /a ERROR_COUNT+=1
    goto :eof
)

echo [SUCCESS] %RUNTIME% -^> %OUTPUT%
goto :eof

:Elapsed
set "S=%~1"
set "E=%~2"

set /a "sh=1%S:~0,2%-100, sm=1%S:~3,2%-100, ss=1%S:~6,2%-100"
set /a "eh=1%E:~0,2%-100, em=1%E:~3,2%-100, es=1%E:~6,2%-100"
set /a "secs=(eh*3600+em*60+es)-(sh*3600+sm*60+ss)"
if !secs! lss 0 set /a "secs+=86400"
set /a "mm=secs/60, ss=secs%%60"

set "ELAPSED=!mm!m !ss!s"
goto :eof
