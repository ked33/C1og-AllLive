[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPackagesDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$SummaryPath = $env:GITHUB_STEP_SUMMARY
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Select complete signed files; never unpack or modify the MSIX bundle.
$source = (Resolve-Path -LiteralPath $AppPackagesDirectory).Path
$bundles = @(Get-ChildItem -LiteralPath $source -Recurse -File -Filter '*.msixbundle')
if ($bundles.Count -ne 1) {
    throw "Expected exactly one MSIX bundle, found $($bundles.Count)."
}
$bundle = $bundles[0]
if ($bundle.Name -notmatch '_x64\.msixbundle$') {
    throw "Expected an x64 bundle: $($bundle.Name)"
}
$certificate = Join-Path $source 'AllLive.cer'
if (-not (Test-Path -LiteralPath $certificate -PathType Leaf)) {
    throw 'AllLive.cer is missing from AppPackages.'
}
$certificateInstaller = Join-Path $PSScriptRoot 'Install-AllLive-Certificate.cmd'
if (-not (Test-Path -LiteralPath $certificateInstaller -PathType Leaf)) {
    throw 'Install-AllLive-Certificate.cmd is missing from the packaging scripts.'
}
$dependencyDirectory = Join-Path $bundle.DirectoryName 'Dependencies/x64'
$dependencies = @(Get-ChildItem -LiteralPath $dependencyDirectory -File -Filter '*.appx')
if ($dependencies.Count -eq 0) {
    throw 'No x64 framework dependencies were produced.'
}

# Refuse to reuse an output directory: stale files must never enter an artifact.
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Artifact output directory already exists: $OutputDirectory"
}
$installerDirectory = Join-Path $OutputDirectory 'Installer'
$dependencyOutputDirectory = Join-Path $OutputDirectory 'Dependencies/x64'
foreach ($directory in @($installerDirectory, $dependencyOutputDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Copy-Item -LiteralPath $bundle.FullName -Destination $installerDirectory
Copy-Item -LiteralPath $certificate -Destination $installerDirectory
Copy-Item -LiteralPath $certificateInstaller -Destination $installerDirectory
foreach ($dependency in $dependencies) {
    Copy-Item -LiteralPath $dependency.FullName -Destination $dependencyOutputDirectory
}

$rows = @(
    '| Artifact | Contents | Uncompressed MiB |'
    '| --- | --- | ---: |'
)
foreach ($artifact in @(
    @{ Name = 'AllLive-UWP-Packages'; Path = $installerDirectory; Contents = 'AllLive.cer + Install-AllLive-Certificate.cmd + MSIX bundle' }
    @{ Name = 'AllLive-UWP-Dependencies-x64'; Path = (Join-Path $OutputDirectory 'Dependencies'); Contents = 'x64 framework packages (first install / offline)' }
)) {
    $files = @(Get-ChildItem -LiteralPath $artifact.Path -Recurse -File)
    $size = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
    $rows += '| {0} | {1} | {2:F2} |' -f $artifact.Name, $artifact.Contents, $size
    Write-Host ('{0}: {1} files, {2:F2} MiB' -f $artifact.Name, $files.Count, $size)
}
if ($SummaryPath) {
    $rows -join "`n" | Out-File -LiteralPath $SummaryPath -Encoding utf8 -Append
}
