@echo off
title SafeChat setup
cd /d "%~dp0"
where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" %*) || (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" %*)
