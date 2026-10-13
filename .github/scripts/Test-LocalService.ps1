#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$universal = [IO.File]::ReadAllText((Join-Path $repo 'AllLive.UWP/Package.appxmanifest'))
[xml]$desktop = [IO.File]::ReadAllText((Join-Path $repo 'AllLive.UWP/Package.Desktop.appxmanifest'))
foreach ($attribute in @('Name', 'Publisher', 'Version')) {
    if ($universal.Package.Identity.GetAttribute($attribute) -ne $desktop.Package.Identity.GetAttribute($attribute)) {
        throw "Manifest identity mismatch: $attribute"
    }
}
# Package publisher ID = first 64 SHA-256 bits of UTF-16LE publisher, encoded as Windows base32.
$hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes($desktop.Package.Identity.Publisher))
$bits = (($hash[0..7] | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join '') + '0'
$alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
$publisherId = -join (0..12 | ForEach-Object { $alphabet[[Convert]::ToInt32($bits.Substring($_ * 5, 5), 2)] })
$family = $desktop.Package.Identity.Name + '_' + $publisherId
$rules = @($desktop.SelectNodes('//*[local-name()="Rule"]'))
if ($rules.Count -ne 2 -or @($rules | Where-Object PackageFamilyName -ne $family).Count -ne 0) {
    throw 'Loopback rules do not match this package family.'
}
if (($rules.Direction | Sort-Object) -join ',' -ne 'in,out') { throw 'Both loopback directions are required.' }
if ($desktop.Package.Dependencies.TargetDeviceFamily.Name -ne 'Windows.Desktop') { throw 'Desktop family is required.' }
if ($universal.Package.Dependencies.TargetDeviceFamily.Name -ne 'Windows.Universal') { throw 'Universal build must remain available.' }
$extension = $desktop.SelectSingleNode('//*[local-name()="Extension" and @Category="windows.fullTrustProcess"]')
if ($extension.Executable -ne 'LocalService\AllLive.SignService.exe') { throw 'Full-trust executable path mismatch.' }
Write-Output 'PASS: desktop/universal manifests and package loopback identity.'

$result = & dotnet run --project (Join-Path $repo '.github/tests/LocalService/LocalService.Tests.csproj') -c Release --no-launch-profile 2>&1
$exitCode = $LASTEXITCODE
$result | Write-Output
if ($exitCode -ne 0 -or 'PASS: All local service tests.' -notin [string[]]$result) {
    throw "Local service tests did not complete (exit $exitCode)."
}
