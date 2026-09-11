@echo off
setlocal
title Claude Watch - pull and build
cd /d "%~dp0"

rem Replaces the old _update dance. Takes the latest from GitHub and rebuilds.
rem Your settings are not in this folder, so nothing here is yours to lose.

where git >nul 2>&1
if errorlevel 1 (
  echo.
  echo   Git is not installed. Install it once with:
  echo.
  echo       winget install Git.Git
  echo.
  echo   then close this window, open a new one, and run this again.
  echo.
  pause
  exit /b 1
)

if not exist ".git" (
  echo.
  echo   This folder is not a clone of the repo, so there is nothing to pull.
  echo   Clone it once instead:
  echo.
  echo       git clone YOUR-REPO-URL "%%USERPROFILE%%\Desktop\claude-watch"
  echo.
  pause
  exit /b 1
)

echo.
echo   Fetching the latest...

git pull --ff-only
if errorlevel 1 (
  echo.
  echo   The pull did not go through. Usually that means this folder has edits
  echo   of its own. Nothing was changed.
  echo.
  pause
  exit /b 1
)

echo.
echo   Building...
echo.

where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -Rebuild) || (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -Rebuild)
