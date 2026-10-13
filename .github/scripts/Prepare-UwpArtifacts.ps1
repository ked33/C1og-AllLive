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
$dependencyDirectory = Join-Path $bundle.DirectoryName 'Dependencies/x64'
$dependencies = @(Get-ChildItem -LiteralPath $dependencyDirectory -File -Filter '*.appx')
if ($dependencies.Count -eq 0) {
    throw 'No x64 framework dependencies were produced.'
}
$symbols = @(Get-ChildItem -LiteralPath $bundle.DirectoryName -File -Filter '*.appxsym')
if ($symbols.Count -eq 0) {
    throw 'No symbol package was produced.'
}

# Refuse to reuse an output directory: stale files must never enter an artifact.
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Artifact output directory already exists: $OutputDirectory"
}
$installerDirectory = Join-Path $OutputDirectory 'Installer'
$dependencyOutputDirectory = Join-Path $OutputDirectory 'Dependencies/x64'
$symbolDirectory = Join-Path $OutputDirectory 'Symbols'
foreach ($directory in @($installerDirectory, $dependencyOutputDirectory, $symbolDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Copy-Item -LiteralPath $bundle.FullName -Destination $installerDirectory
Copy-Item -LiteralPath $certificate -Destination $installerDirectory
foreach ($dependency in $dependencies) {
    Copy-Item -LiteralPath $dependency.FullName -Destination $dependencyOutputDirectory
}
foreach ($symbol in $symbols) {
    Copy-Item -LiteralPath $symbol.FullName -Destination $symbolDirectory
}

$rows = @(
    '| Artifact | Contents | Uncompressed MiB |'
    '| --- | --- | ---: |'
)
foreach ($artifact in @(
    @{ Name = 'AllLive-UWP-Packages'; Path = $installerDirectory; Contents = 'AllLive.cer + MSIX bundle' }
    @{ Name = 'AllLive-UWP-Dependencies-x64'; Path = (Join-Path $OutputDirectory 'Dependencies'); Contents = 'x64 framework packages (first install / offline)' }
    @{ Name = 'AllLive-UWP-Symbols-x64'; Path = $symbolDirectory; Contents = 'Debugging symbols only' }
)) {
    $files = @(Get-ChildItem -LiteralPath $artifact.Path -Recurse -File)
    $size = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
    $rows += '| {0} | {1} | {2:F2} |' -f $artifact.Name, $artifact.Contents, $size
    Write-Host ('{0}: {1} files, {2:F2} MiB' -f $artifact.Name, $files.Count, $size)
}
if ($SummaryPath) {
    $rows -join "`n" | Out-File -LiteralPath $SummaryPath -Encoding utf8 -Append
}
