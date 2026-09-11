@echo off
title Claude Watch - rebuild
cd /d "%~dp0"
where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -Rebuild) || (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -Rebuild)
