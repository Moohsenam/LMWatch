@echo off
setlocal enabledelayedexpansion
title SafeChat - build the installer
cd /d "%~dp0"

rem Builds SafeChat-Setup-<version>.exe out of SafeChat.zip sitting next to
rem this file, and leaves it in this same folder. Nothing is uploaded: run it,
rem try the installer yourself, and put it on the server afterwards from the
rem panel's نسخه‌ها tab.

set "ZIP=%~dp0SafeChat.zip"
set "WORK=%USERPROFILE%\Desktop\SafeChat"

if not exist "%ZIP%" (
  echo.
  echo   SafeChat.zip is not next to this file.
  echo   Both have to sit in the same folder:  %~dp0
  echo.
  pause
  exit /b 1
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo.
  echo   The .NET 8 SDK is not installed. Run this once:
  echo       winget install Microsoft.DotNet.SDK.8
  echo   Then close this window, open a new one, and run this again.
  echo.
  pause
  exit /b 1
)

set "VER="
echo.
set /p VER=  Version number (press Enter for 1.0.0):
if "!VER!"=="" set "VER=1.0.0"

echo.
echo   Building SafeChat !VER!
echo   This takes a few minutes. The .NET runtime goes inside the installer,
echo   so it is a large file and the compression is slow.
echo.

if exist "%WORK%" rmdir /s /q "%WORK%" 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Expand-Archive -LiteralPath '%ZIP%' -DestinationPath '%WORK%' -Force"
if errorlevel 1 goto failed

cd /d "%WORK%"

where pwsh >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -ExecutionPolicy Bypass -File "tools\release.ps1" -Version !VER! -NoUpload
) else (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\release.ps1" -Version !VER! -NoUpload
)

set "SETUP=%WORK%\build\SafeChat-Setup-!VER!.exe"

if not exist "!SETUP!" (
  echo.
  echo   The installer did not come out. The messages above say why.
  echo.
  pause
  exit /b 1
)

copy /y "!SETUP!" "%~dp0" >nul

echo.
echo   Done.  %~dp0SafeChat-Setup-!VER!.exe
echo.
echo   Run it to check it installs and opens, then upload it in the panel
echo   under نسخه‌ها, or run tools\release.ps1 without -NoUpload next time
echo   and it goes up on its own.
echo.
pause
exit /b 0

:failed
echo.
echo   Unpacking failed. Nothing was changed.
echo.
pause
exit /b 1
