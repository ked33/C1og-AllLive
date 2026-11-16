#requires -Version 5.1
[CmdletBinding()]
param([switch]$NoPause)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# AppX deployment uses the built-in Windows PowerShell host. Keep the current
# user when launched from PowerShell 7 or a 32-bit process; only certutil elevates.
if ($PSVersionTable.PSEdition -ne 'Desktop' -or ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess)) {
    $systemDirectory = if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { 'Sysnative' } else { 'System32' }
    $windowsPowerShell = Join-Path $env:SystemRoot ($systemDirectory + '\WindowsPowerShell\v1.0\powershell.exe')
    $forwardArguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($NoPause) { $forwardArguments += '-NoPause' }
    & $windowsPowerShell @forwardArguments
    exit $LASTEXITCODE
}

try {
    $certificatePath = Join-Path $PSScriptRoot 'AllLive.cer'
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw 'AllLive.cer not found. Extract the complete installation package first.'
    }
    $bundles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'AllLive.UWP*_x64.msixbundle' -File)
    if ($bundles.Count -ne 1) {
        throw 'Expected one AllLive x64 MSIX bundle beside this script. Extract each release into its own folder.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($bundles[0].FullName)
    try {
        $entry = $archive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
        if ($null -eq $entry) { throw 'The MSIX bundle manifest is missing.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    $identity = $manifest.SelectSingleNode('/*[local-name()="Bundle"]/*[local-name()="Identity"]')
    if ($null -eq $identity -or $identity.GetAttribute('Name') -ne '5421.24244EC421563') {
        throw 'This bundle is not the expected AllLive application.'
    }
    $version = [version]$identity.GetAttribute('Version')
    $name = $identity.GetAttribute('Name')
    $publisher = $identity.GetAttribute('Publisher')

    $dependencyFiles = @(
        foreach ($relative in @('Dependencies\x64', 'x64')) {
            $directory = Join-Path $PSScriptRoot $relative
            if (Test-Path -LiteralPath $directory -PathType Container) {
                Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Extension -in @('.appx', '.msix') }
            }
        }
    )
    if ($dependencyFiles.Count -eq 0) {
        Write-Host '[INFO] No local dependency packages found. Existing installed frameworks can still be used.'
        Write-Host 'For a first install or a missing-framework error, download AllLive-UWP-Dependencies-x64 from the SAME build:'
        Write-Host 'https://github.com/ked33/C1og-AllLive/actions'
        Write-Host 'Extract its x64 folder beside Install-AllLive.ps1 (x64\*.appx or Dependencies\x64\*.appx), then run this script again.'
    }

    # Elevate only the certificate operation. AppX registration belongs to the
    # user who launched this script, even when UAC uses another admin account.
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try {
        if ($certificate.Subject -ne $publisher) { throw 'AllLive.cer does not match the bundle publisher.' }
        $trusted = Test-Path -LiteralPath ('Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint)
    } finally { $certificate.Dispose() }
    if (-not $trusted) {
        Write-Host 'Installing certificate into Local Computer - Trusted People. Accept the administrator prompt...'
        $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\certutil.exe') `
            -Verb RunAs -WindowStyle Hidden -ArgumentList ('-addstore TrustedPeople "' + $certificatePath + '"') -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Certificate installation failed. Code: $($process.ExitCode)" }
    }
    Write-Host '[OK] Certificate installed.'

    $installed = @(Get-AppxPackage -Name $name | Where-Object { $_.Publisher -eq $publisher } |
        Sort-Object { [version]$_.Version } -Descending)
    if ($installed.Count -gt 0 -and [version]$installed[0].Version -ge $version) {
        Write-Host ('[OK] AllLive {0} is already installed. This package is {1}; no downgrade is needed.' -f $installed[0].Version, $version)
        exit 0
    }

    $dependencyPaths = @()
    foreach ($dependency in $dependencyFiles) {
        # An installed newer framework must never be downgraded by an older release.
        $zip = [IO.Compression.ZipFile]::OpenRead($dependency.FullName)
        try {
            $entry = $zip.GetEntry('AppxManifest.xml')
            if ($null -eq $entry) { throw "Dependency manifest missing: $($dependency.Name)" }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$dependencyManifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        $dependencyIdentity = $dependencyManifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
        if ($null -eq $dependencyIdentity) { throw "Dependency identity missing: $($dependency.Name)" }
        $satisfied = @(Get-AppxPackage -Name $dependencyIdentity.GetAttribute('Name') | Where-Object {
            $_.Publisher -eq $dependencyIdentity.GetAttribute('Publisher') -and
            $_.Architecture.ToString() -eq $dependencyIdentity.GetAttribute('ProcessorArchitecture') -and
            [version]$_.Version -ge [version]$dependencyIdentity.GetAttribute('Version')
        })
        if ($satisfied.Count -eq 0) { $dependencyPaths += $dependency.FullName }
    }
    Write-Host ("Installing/updating AllLive {0} for the current user. Running AllLive windows may close..." -f $version)
    $parameters = @{ Path = $bundles[0].FullName; ForceApplicationShutdown = $true; ErrorAction = 'Stop' }
    if ($dependencyPaths.Count -gt 0) { $parameters.DependencyPath = @($dependencyPaths | Select-Object -Unique) }
    Add-AppxPackage @parameters
    $verified = @(Get-AppxPackage -Name $name | Where-Object { $_.Publisher -eq $publisher -and [version]$_.Version -ge $version })
    if ($verified.Count -eq 0) { throw 'Installation finished but the expected AllLive version was not registered for this user.' }
    Write-Host ('[OK] AllLive {0} installed. Open AllLive from the Start menu.' -f $version)
    exit 0
} catch {
    Write-Host ('[ERROR] ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'Installation did not complete. Keep Install-AllLive.ps1, AllLive.cer and the MSIX bundle together and retry.'
    Write-Host 'If frameworks are missing, download AllLive-UWP-Dependencies-x64 from the same Actions build and extract its x64 folder beside this script.'
    exit 1
} finally {
    if (-not $NoPause) { [void](Read-Host 'Press Enter to close') }
}
