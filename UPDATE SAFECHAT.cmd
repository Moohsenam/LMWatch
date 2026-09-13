@echo off
setlocal enabledelayedexpansion
title SafeChat - install or update
cd /d "%USERPROFILE%\Desktop"

rem Installs SafeChat on a machine that has never had it, and updates one that
rem has. Both cases come straight from GitHub, so there is nothing to download
rem by hand and nothing to keep in step.

set "REPO=https://github.com/Moohsenam/LMWatch.git"
set "CLONE=%USERPROFILE%\Desktop\claude-watch"
set "FRESH=0"

where git >nul 2>&1
if errorlevel 1 (
  echo.
  echo   Git is not installed. Run this once:  winget install Git.Git
  echo   Then close this window, open a new one, and run this again.
  echo.
  pause
  exit /b 1
)

if not exist "%CLONE%\.git" (
  set "FRESH=1"
  echo.
  echo   First time on this machine. Getting the code...
  echo   If it asks who you are:  username Moohsenam, password your GitHub token.
  echo.
  git clone "%REPO%" "%CLONE%"
  if errorlevel 1 goto failed
  goto build
)

cd /d "%CLONE%"

for /f %%C in ('git rev-parse --short HEAD') do set "BEFORE=%%C"

echo.
echo   Taking the new commits...
git pull --ff-only
if errorlevel 1 (
  echo.
  echo   Could not fast-forward. This copy has changes of its own, so nothing
  echo   was touched. To throw them away and take the published version:
  echo.
  echo       git -C "%CLONE%" fetch origin ^&^& git -C "%CLONE%" reset --hard origin/main
  echo.
  pause
  exit /b 1
)

for /f %%C in ('git rev-parse --short HEAD') do set "AFTER=%%C"

echo.
if "!BEFORE!"=="!AFTER!" (
  echo   Still at !AFTER!. Nothing new was published, so this will rebuild
  echo   the same app you already have.
) else (
  echo   Updated:  !BEFORE!  ^-^>  !AFTER!
)
echo.

:build
cd /d "%CLONE%"
echo   Building...
echo.

rem A first install also wants the Desktop and Start Menu shortcuts, which
rem -Rebuild deliberately skips.
if "!FRESH!"=="1" (set "ARGS=") else (set "ARGS=-Rebuild")

where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\setup.ps1" !ARGS!) || (powershell -NoProfile -ExecutionPolicy Bypass -File "tools\setup.ps1" !ARGS!)
goto done

:failed
echo.
echo   That did not work. Nothing was changed.
echo.
pause
exit /b 1

:done
echo.
pause
