@echo off
setlocal DisableDelayedExpansion
title AllLive certificate installer
set "ALLLIVE_CERT=%~dp0AllLive.cer"

if not exist "%ALLLIVE_CERT%" (
    echo [ERROR] AllLive.cer not found. Extract the complete installation package first.
    pause
    exit /b 1
)

echo Installing AllLive.cer into Local Computer - Trusted People...
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command ^
 "$ErrorActionPreference = 'Stop'; try { $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\certutil.exe') -Verb RunAs -WindowStyle Hidden -ArgumentList ('-addstore TrustedPeople ' + [char]34 + $env:ALLLIVE_CERT + [char]34) -Wait -PassThru; exit $p.ExitCode } catch { Write-Host $_.Exception.Message; exit 1 }"

set "ALLLIVE_EXIT=%errorlevel%"
if "%ALLLIVE_EXIT%"=="0" (
    echo [OK] Certificate installed. You can now install the MSIX bundle.
) else (
    echo [ERROR] Installation failed or canceled. Code: %ALLLIVE_EXIT%
)

pause
exit /b %ALLLIVE_EXIT%
