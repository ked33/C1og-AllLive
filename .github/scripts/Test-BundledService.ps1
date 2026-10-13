#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPackagesDirectory,
    [Parameter(Mandatory)][string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$bundles = @(Get-ChildItem -LiteralPath $AppPackagesDirectory -Filter '*.msixbundle' -Recurse -File)
if ($bundles.Count -ne 1) { throw 'Expected one MSIX bundle.' }
$bundle = [IO.Compression.ZipFile]::OpenRead($bundles[0].FullName)
try {
    $packages = @($bundle.Entries | Where-Object { $_.Name -match '_x64\.(appx|msix)$' })
    if ($packages.Count -ne 1) { throw 'Expected one x64 application inside the bundle.' }
    $memory = [IO.MemoryStream]::new()
    $input = $packages[0].Open()
    try { $input.CopyTo($memory) } finally { $input.Dispose() }
    $memory.Position = 0
    $package = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
            $name = 'LocalService/' + [IO.Path]::GetRelativePath($publish, $file.FullName).Replace('\', '/')
            $entry = $package.GetEntry($name)
            if ($null -eq $entry -or $entry.Length -ne $file.Length) { throw "Missing/incomplete packaged service file: $name" }
        }
        $reader = [IO.StreamReader]::new($package.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.Package.Dependencies.TargetDeviceFamily.Name -ne 'Windows.Desktop') { throw 'Wrong packaged manifest.' }
        if ($null -eq $manifest.SelectSingleNode('//*[local-name()="Capability" and @Name="runFullTrust"]')) { throw 'Missing runFullTrust.' }
        $config = Get-Content -LiteralPath (Join-Path $publish 'AllLive.SignService.runtimeconfig.json') -Raw | ConvertFrom-Json
        if (-not $config.runtimeOptions.includedFrameworks) { throw 'Service is not self-contained.' }
        Write-Output 'PASS: complete self-contained service payload and desktop manifest inside the signed MSIX.'
    }
    finally { $package.Dispose(); $memory.Dispose() }
}
finally { $bundle.Dispose() }
