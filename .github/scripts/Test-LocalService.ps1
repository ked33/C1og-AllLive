#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
& (Join-Path $PSScriptRoot 'Test-PackageManifests.ps1')

$result = & dotnet run --project (Join-Path $repo '.github/tests/LocalService/LocalService.Tests.csproj') -c Release --no-launch-profile 2>&1
$exitCode = $LASTEXITCODE
$result | Write-Output
if ($exitCode -ne 0 -or 'PASS: All local service tests.' -notin [string[]]$result) {
    throw "Local service tests did not complete (exit $exitCode)."
}
