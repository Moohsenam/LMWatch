@echo off
setlocal enabledelayedexpansion
title SafeChat - update
cd /d "%USERPROFILE%\Desktop"

set "CLONE=%USERPROFILE%\Desktop\claude-watch"
set "BUNDLE="

rem The newest SafeChat-update*.bundle on the Desktop wins. A browser that
rem saved the new one as "(1)" no longer means the old one gets used.
for /f "delims=" %%F in ('dir /b /o-d "SafeChat-update*.bundle" 2^>nul') do (
  if not defined BUNDLE set "BUNDLE=%USERPROFILE%\Desktop\%%F"
)

where git >nul 2>&1
if errorlevel 1 (
  echo.
  echo   Git is not installed. Run this once:  winget install Git.Git
  echo   Then close this window, open a new one, and run this again.
  echo.
  pause
  exit /b 1
)

if not defined BUNDLE (
  echo.
  echo   No SafeChat-update bundle on the Desktop.
  echo.
  pause
  exit /b 1
)

echo.
echo   Bundle:  !BUNDLE!
echo   Clone:   %CLONE%
echo.

if not exist "%CLONE%\.git" (
  echo   No clone here yet, making one...
  git clone "!BUNDLE!" "%CLONE%"
  if errorlevel 1 goto failed
  cd /d "%CLONE%"
  git remote set-url origin "https://github.com/Moohsenam/LMWatch.git"
  goto build
)

cd /d "%CLONE%"

for /f %%C in ('git rev-parse --short HEAD') do set "BEFORE=%%C"

echo   Taking the new commits...
git fetch "!BUNDLE!" main
if errorlevel 1 goto failed

git merge --ff-only FETCH_HEAD
if errorlevel 1 (
  echo.
  echo   This clone has changes of its own, so nothing was merged and
  echo   nothing was lost. To throw those changes away and take the new
  echo   version, run this one line and then run me again:
  echo.
  echo       git -C "%CLONE%" reset --hard FETCH_HEAD
  echo.
  pause
  exit /b 1
)

for /f %%C in ('git rev-parse --short HEAD') do set "AFTER=%%C"

echo.
if "!BEFORE!"=="!AFTER!" (
  echo   Still at !AFTER!. This bundle has nothing newer in it, so the
  echo   build below will be the same app you already have.
) else (
  echo   Updated:  !BEFORE!  ^-^>  !AFTER!
)
echo.

:build
echo   Building...
echo.
where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\setup.ps1" -Rebuild) || (powershell -NoProfile -ExecutionPolicy Bypass -File "tools\setup.ps1" -Rebuild)
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
