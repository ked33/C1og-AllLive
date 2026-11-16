@echo off
setlocal DisableDelayedExpansion
title AllLive installer
set "ALLLIVE_INSTALLER=%~dp0Install-AllLive.ps1"
set "ALLLIVE_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if defined PROCESSOR_ARCHITEW6432 set "ALLLIVE_POWERSHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"

if not exist "%ALLLIVE_INSTALLER%" (
    echo [ERROR] Install-AllLive.ps1 not found. Extract the complete installation package first.
    pause
    exit /b 1
)

"%ALLLIVE_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%ALLLIVE_INSTALLER%"
set "ALLLIVE_EXIT=%errorlevel%"

pause
exit /b %ALLLIVE_EXIT%
