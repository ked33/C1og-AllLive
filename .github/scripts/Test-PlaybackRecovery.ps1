#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sources = @(
    (Join-Path $repoRoot 'AllLive.UWP\Helper\PlaybackOperationGate.cs'),
    (Join-Path $repoRoot 'AllLive.Core\LiveStatusParser.cs'),
    (Join-Path $repoRoot '.github\tests\PlaybackRecoveryTests.cs')
)
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File | ForEach-Object FullName)
$references += Join-Path $PSHOME 'Newtonsoft.Json.dll'
# PowerShell ships Newtonsoft.Json for an older System.Runtime; use the host's compatible runtime.
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/nowarn:1701'
[AllLive.Tests.PlaybackRecoveryTests]::RunAsync().GetAwaiter().GetResult()

# Syntax and project inclusion checks do not replace the UWP/WinRT cloud build.
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'AllLive.UWP\AllLive.UWP.csproj') -Raw
$uwpSources = @($project.Project.ItemGroup.Compile | Where-Object Include | ForEach-Object {
    Join-Path $repoRoot ('AllLive.UWP\' + $_.Include)
})
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'AllLive.Core') -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object FullName)
$syntaxFiles = @($uwpSources + $coreSources | Sort-Object -Unique)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
    [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp7_3).WithPreprocessorSymbols(
    [string[]]@('TRACE', 'WINDOWS_UWP', 'NETFX_CORE', 'BUNDLED_LOCAL_SERVICE'))
$errors = @()
foreach ($path in $syntaxFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Compile source is missing: $path" }
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path), $options, $path)
    $errors += @($tree.GetDiagnostics() | Where-Object { $_.Severity -eq 'Error' } | ForEach-Object ToString)
}
if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
Write-Output "PASS: C# 7.3 syntax and Compile paths ($($syntaxFiles.Count) source files)."
