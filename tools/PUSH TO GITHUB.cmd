@echo off
setlocal enabledelayedexpansion
title SafeChat - push to GitHub
cd /d "%~dp0"

rem Put this file and SafeChat-update.bundle in the SAME folder, then run it.
rem It takes the code out of the bundle and pushes it to GitHub. Nothing on
rem this machine is changed except a temporary folder it makes and removes.

set "REPO=https://github.com/Moohsenam/LMWatch.git"
set "WORK=safechat-push-temp"
set "BUNDLE="

for /f "delims=" %%F in ('dir /b /o-d "SafeChat-update*.bundle" 2^>nul') do (
  if not defined BUNDLE set "BUNDLE=%%F"
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
  echo   No SafeChat-update bundle next to this file.
  echo   Put SafeChat-update.bundle in this same folder and run me again.
  echo.
  echo   This folder is:  %~dp0
  echo.
  pause
  exit /b 1
)

echo.
echo   Bundle:  !BUNDLE!
echo   Repo:    %REPO%
echo.

if exist "%WORK%" rmdir /s /q "%WORK%"

echo   Unpacking...
git clone --quiet "!BUNDLE!" "%WORK%"
if errorlevel 1 goto failed

cd "%WORK%"
git remote set-url origin "%REPO%"

echo.
echo   Pushing. If it asks who you are:
echo       username:  Moohsenam
echo       password:  your GitHub token (not your account password)
echo.

git push origin main
if errorlevel 1 goto pushfailed

cd ..
rmdir /s /q "%WORK%"

echo.
echo   Done. The code is on GitHub.
echo.
echo   Now on the server, run these two lines:
echo       git -C /srv/claude-watch pull --ff-only
echo       bash /srv/claude-watch/server/bootstrap.sh
echo.
pause
exit /b 0

:pushfailed
cd ..
echo.
echo   The push did not go through. Nothing was lost - the unpacked copy is
echo   still in the "%WORK%" folder next to this file, so you can try again
echo   from there with:  git push origin main
echo.
pause
exit /b 1

:failed
echo.
echo   Could not read the bundle. Nothing was changed.
echo.
pause
exit /b 1
