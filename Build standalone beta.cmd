@echo off
title Claude Watch - standalone beta
cd /d "%~dp0"
where pwsh >nul 2>&1 && (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -SelfContained) || (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\setup.ps1" -SelfContained)
