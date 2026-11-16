#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoLogo -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'Native Windows PowerShell installer checks failed.' }
    exit 0
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('AllLive installer test ' + [guid]::NewGuid().ToString('N') + (" & '[test] " + [char]0x4E2D + [char]0x6587))
New-Item -ItemType Directory -Path $fixture | Out-Null
function New-TestArchive([string]$Path, [string]$Entry, [string]$Xml) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($zip.CreateEntry($Entry).Open())
        try { $writer.Write($Xml) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
}
try {
    $installer = Join-Path $fixture 'Install-AllLive.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-AllLive.ps1') -Destination $installer
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=AllLive Installer Test', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        try { [IO.File]::WriteAllBytes((Join-Path $fixture 'AllLive.cer'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)) }
        finally { $certificate.Dispose() }
    } finally { $rsa.Dispose() }
    $bundlePath = Join-Path $fixture 'AllLive.UWP_2.3.82.0_x64.msixbundle'
    New-TestArchive $bundlePath 'AppxMetadata/AppxBundleManifest.xml' '<Bundle xmlns="http://schemas.microsoft.com/appx/2013/bundle"><Identity Name="5421.24244EC421563" Publisher="CN=AllLive Installer Test" Version="2.3.82.0" /></Bundle>'
    $dependencyDirectory = Join-Path $fixture 'Dependencies/x64'
    New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
    New-TestArchive (Join-Path $dependencyDirectory 'Framework.appx') 'AppxManifest.xml' '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Test.Framework" Publisher="CN=Test Framework" Version="1.0.0.0" ProcessorArchitecture="x64" /></Package>'

    # Isolated runspaces shadow every mutating Windows command. No actual
    # certificate import, elevation, application shutdown or AppX registration.
    $scenarios = @(
        @{ Name = 'fresh'; Installed = ''; Framework = $false; Trusted = $false; Cancel = $false; FailInstall = $false; ExpectedExit = 0; ExpectedInstall = 1; ExpectedElevation = 1; ExpectedDependencies = 1 }
        @{ Name = 'upgrade'; Installed = '2.3.81.0'; Framework = $true; Trusted = $true; Cancel = $false; FailInstall = $false; ExpectedExit = 0; ExpectedInstall = 1; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'same'; Installed = '2.3.82.0'; Framework = $true; Trusted = $true; Cancel = $false; FailInstall = $false; ExpectedExit = 0; ExpectedInstall = 0; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'newer'; Installed = '2.3.83.0'; Framework = $true; Trusted = $true; Cancel = $false; FailInstall = $false; ExpectedExit = 0; ExpectedInstall = 0; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'cancel'; Installed = ''; Framework = $false; Trusted = $false; Cancel = $true; FailInstall = $false; ExpectedExit = 1; ExpectedInstall = 0; ExpectedElevation = 1; ExpectedDependencies = 0 }
        @{ Name = 'appx failure'; Installed = ''; Framework = $false; Trusted = $true; Cancel = $false; FailInstall = $true; ExpectedExit = 1; ExpectedInstall = 1; ExpectedElevation = 0; ExpectedDependencies = 1 }
        @{ Name = 'no dependencies update'; Installed = '2.3.81.0'; Framework = $true; Trusted = $true; Cancel = $false; FailInstall = $false; ExpectedExit = 0; ExpectedInstall = 1; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'no dependencies fresh'; Installed = ''; Framework = $false; Trusted = $true; Cancel = $false; FailInstall = $true; ExpectedExit = 1; ExpectedInstall = 1; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'missing certificate'; Installed = ''; Framework = $false; Trusted = $false; Cancel = $false; FailInstall = $false; ExpectedExit = 1; ExpectedInstall = 0; ExpectedElevation = 0; ExpectedDependencies = 0 }
        @{ Name = 'ambiguous bundles'; Installed = ''; Framework = $false; Trusted = $false; Cancel = $false; FailInstall = $false; ExpectedExit = 1; ExpectedInstall = 0; ExpectedElevation = 0; ExpectedDependencies = 0 }
    )
    foreach ($scenario in $scenarios) {
        $extraBundle = Join-Path $fixture 'AllLive.UWP_duplicate_x64.msixbundle'
        if ($scenario.Name -eq 'ambiguous bundles') { Copy-Item -LiteralPath $bundlePath -Destination $extraBundle }
        $runner = [powershell]::Create()
        try {
            [void]$runner.AddScript({
                param($Installer, $Case)
                $ErrorActionPreference = 'Stop'
                $global:Case = $Case
                $global:InstallCount = 0
                $global:ElevationCount = 0
                $global:DependencyCount = 0
                $global:PauseCount = 0
                function global:Read-Host { param($Prompt); $global:PauseCount++; return "" }
                function global:Test-Path {
                    param($LiteralPath, $PathType)
                    if ($global:Case.Name -like 'no dependencies*' -and $LiteralPath.EndsWith('x64')) { return $false }
                    if ($LiteralPath -like 'Cert:*') { return $global:Case.Trusted }
                    if ($global:Case.Name -eq 'missing certificate' -and $LiteralPath.EndsWith('AllLive.cer')) { return $false }
                    if ($PathType) { return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath -PathType $PathType }
                    return Microsoft.PowerShell.Management\Test-Path -LiteralPath $LiteralPath
                }
                function global:Start-Process {
                    param($FilePath, $Verb, $WindowStyle, $ArgumentList, [switch]$Wait, [switch]$PassThru)
                    $global:ElevationCount++
                    if ($Verb -ne 'RunAs' -or $WindowStyle -ne 'Hidden' -or [IO.Path]::GetFileName($FilePath) -ne 'certutil.exe') { throw 'Wrong elevated operation.' }
                    if ($ArgumentList -ne ('-addstore TrustedPeople "' + (Join-Path (Split-Path -LiteralPath $Installer) 'AllLive.cer') + '"')) { throw 'Certificate path quoting failed.' }
                    if ($global:Case.Cancel) { throw 'Simulated UAC cancellation.' }
                    return [pscustomobject]@{ ExitCode = 0 }
                }
                function global:Get-AppxPackage {
                    param($Name)
                    if ($Name -eq 'Test.Framework' -and $global:Case.Framework) {
                        return [pscustomobject]@{ Publisher = 'CN=Test Framework'; Architecture = 'X64'; Version = '2.0.0.0' }
                    }
                    if ($Name -eq '5421.24244EC421563') {
                        $version = if ($global:InstallCount -gt 0) { '2.3.82.0' } else { $global:Case.Installed }
                        if ($version) { return [pscustomobject]@{ Publisher = 'CN=AllLive Installer Test'; Version = $version } }
                    }
                }
                function global:Add-AppxPackage {
                    param($Path, $DependencyPath, [switch]$ForceApplicationShutdown, $ErrorAction)
                    $global:InstallCount++
                    if ($Path -ne (Join-Path (Split-Path -LiteralPath $Installer) 'AllLive.UWP_2.3.82.0_x64.msixbundle') -or -not $ForceApplicationShutdown) { throw 'Incorrect bundle path/update options.' }
                    $global:DependencyCount = @($DependencyPath | Where-Object { $_ }).Count
                    if ($global:Case.FailInstall) { throw 'Simulated AppX installation failure.' }
                }
                & $Installer
                [pscustomobject]@{ ExitCode = $LASTEXITCODE; Installs = $global:InstallCount; Elevations = $global:ElevationCount; Dependencies = $global:DependencyCount; Pauses = $global:PauseCount }
            }).AddArgument($installer).AddArgument($scenario)
            $results = @($runner.Invoke())
            if ($runner.Streams.Error.Count -gt 0 -or $runner.InvocationStateInfo.State -ne 'Completed') { throw ("Runspace error: " + $runner.InvocationStateInfo.State + " / " + $runner.InvocationStateInfo.Reason + " / result=" + ($results | ConvertTo-Json -Compress) + " / info=" + ($runner.Streams.Information -join "`n") + " / " + ($runner.Streams.Error -join "`n")) }
            $result = $results[-1]
            if ($result.ExitCode -ne $scenario.ExpectedExit -or $result.Installs -ne $scenario.ExpectedInstall -or
                $result.Elevations -ne $scenario.ExpectedElevation -or $result.Dependencies -ne $scenario.ExpectedDependencies -or $result.Pauses -ne 1) {
                throw ('Installer scenario {0} failed: {1}; output: {2}' -f $scenario.Name, ($result | ConvertTo-Json -Compress), ($runner.Streams.Information -join "`n"))
            }
            if ($scenario.Name -like 'no dependencies*') {
                $messages = $runner.Streams.Information -join "`n"
                foreach ($expected in @('AllLive-UWP-Dependencies-x64', 'https://github.com/ked33/C1og-AllLive/actions', 'x64\*.appx')) {
                    if (-not $messages.Contains($expected)) { throw ('Missing dependency download guidance: ' + $expected) }
                }
            }
        } finally { $runner.Dispose() }
    }

    # Verify the main artifact contains only three files and dependencies stay separate.
    $source = Join-Path $fixture 'build'
    $sourceDependencies = Join-Path $source 'Dependencies/x64'
    New-Item -ItemType Directory -Path $sourceDependencies -Force | Out-Null
    Copy-Item -LiteralPath $bundlePath, (Join-Path $fixture 'AllLive.cer') -Destination $source
    Copy-Item -LiteralPath (Join-Path $dependencyDirectory 'Framework.appx') -Destination $sourceDependencies
    $output = Join-Path $fixture 'output'
    & (Join-Path $PSScriptRoot 'Prepare-UwpArtifacts.ps1') -AppPackagesDirectory $source -OutputDirectory $output -SummaryPath ''
    foreach ($file in @('Install-AllLive.ps1', 'AllLive.cer', 'AllLive.UWP_2.3.82.0_x64.msixbundle')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output "Installer/$file"))) { throw "Missing installer artifact: $file" }
    }

    if (@(Get-ChildItem -LiteralPath (Join-Path $output 'Installer') -Force).Count -ne 3 -or
        -not (Test-Path -LiteralPath (Join-Path $output 'Dependencies/x64/Framework.appx'))) {
        throw 'Main artifact must have exactly three files and dependencies must be a separate artifact.'
    }

    # Run the standalone entry point in Windows PowerShell with a missing
    # certificate: this exercises parsing, special paths and the -NoPause switch.
    $entryDirectory = Join-Path $fixture 'standalone'
    New-Item -ItemType Directory -Path $entryDirectory | Out-Null
    $entry = Join-Path $entryDirectory 'Install-AllLive.ps1'
    Copy-Item -LiteralPath $installer -Destination $entry
    $hosts = @((Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'))
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($pwsh) { $hosts += $pwsh.Source }
    foreach ($hostPath in $hosts) {
        $process = [Diagnostics.Process]::new()
        try {
            $process.StartInfo.FileName = $hostPath
            $process.StartInfo.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $entry + '" -NoPause'
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.CreateNoWindow = $true
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Standalone installer timed out.' }
            $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 1 -or $text -notmatch 'AllLive.cer not found') {
                throw "Standalone installer failed: exit=$($process.ExitCode); $text"
            }
        } finally { $process.Dispose() }
    }
    Write-Output 'PASS: installer first install/update, version checks, dependency selection, UAC cancellation, failure paths, special paths, missing-dependency guidance and separate artifacts (mocked Windows mutations).'
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedFixture).StartsWith('AllLive installer test ')) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
