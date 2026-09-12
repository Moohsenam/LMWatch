@echo off
title SafeChat removal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\uninstall.ps1"
