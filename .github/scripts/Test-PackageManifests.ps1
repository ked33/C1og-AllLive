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
# UWP targets rewrite Windows.Universal placeholders, but not the explicit desktop family.
# MSIX payloads require Windows 10 1809 or newer; use the actual project minimum (1903).
[xml]$project = [IO.File]::ReadAllText((Join-Path $repo 'AllLive.UWP/AllLive.UWP.csproj'))
$projectMinimum = [version]$project.SelectSingleNode('//*[local-name()="TargetPlatformMinVersion"]').InnerText
$desktopMinimum = [version]$desktop.Package.Dependencies.TargetDeviceFamily.MinVersion
$desktopMaximum = [version]$desktop.Package.Dependencies.TargetDeviceFamily.MaxVersionTested
if ($desktopMinimum -lt [version]'10.0.17763.0' -or $desktopMinimum -ne $projectMinimum -or $desktopMaximum -lt $desktopMinimum) {
    throw 'Desktop manifest must explicitly declare the project minimum and support MSIX (Windows 10 1809+).'
}
$extension = $desktop.SelectSingleNode('//*[local-name()="Extension" and @Category="windows.fullTrustProcess"]')
if ($extension.Executable -ne 'LocalService\AllLive.SignService.exe') { throw 'Full-trust executable path mismatch.' }
$release = Get-Content -LiteralPath (Join-Path $repo 'AllLive.UWP/version.json') -Raw | ConvertFrom-Json
$packageVersion = [version]$desktop.Package.Identity.Version
if ($release.version -ne $packageVersion.ToString(3)) { throw 'Release JSON version does not match the package manifests.' }
$expectedCode = $packageVersion.Major * 10000 + $packageVersion.Minor * 100 + $packageVersion.Build
if ($release.versionCode -ne $expectedCode) { throw 'Release JSON versionCode does not match the package version.' }
Write-Output ('PASS: release version {0}, desktop/universal manifests and package loopback identity.' -f $packageVersion)
