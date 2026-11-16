#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PackageManifests.ps1')
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sources = @(
    (Join-Path $repoRoot 'AllLive.UWP\Helper\PlaybackOperationGate.cs'),
    (Join-Path $repoRoot 'AllLive.UWP\Helper\PlaybackCdnPreferenceStore.cs'),
    (Join-Path $repoRoot 'AllLive.Core\LiveStatusParser.cs'),
    (Join-Path $repoRoot 'AllLive.Core\BilibiliRoomFilter.cs'),
    (Join-Path $repoRoot 'AllLive.Core\PlaybackHealthTracker.cs'),
    (Join-Path $repoRoot 'AllLive.Core\DouyinRequestGate.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\CoreDebug.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\HttpUtil.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\CoreConfig.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\DouyuAccountState.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\DouyuAuthClient.cs'),
    (Join-Path $repoRoot 'AllLive.Core\DouyuPlaybackClient.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Models\LivePlayQuality.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Models\LivePlayUrl.cs'),
    (Join-Path $repoRoot '.github\tests\DouyuAccountTests.cs'),
    (Join-Path $repoRoot '.github\tests\NetworkOptimizationTests.cs'),
    (Join-Path $repoRoot '.github\tests\PlaybackRecoveryTests.cs'),
    (Join-Path $repoRoot '.github\tests\DouyinRequestGateTests.cs'),
    (Join-Path $repoRoot '.github\tests\BilibiliRoomFilterTests.cs'),
    (Join-Path $repoRoot '.github\tests\HlsPlaybackTests.cs'),
    (Join-Path $repoRoot '.github\tests\HlsPreferenceStoreTests.cs')
)
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File | ForEach-Object FullName)
$references += Join-Path $PSHOME 'Newtonsoft.Json.dll'
# PowerShell ships Newtonsoft.Json for an older System.Runtime; use the host's compatible runtime.
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/nowarn:1701'
[AllLive.Tests.PlaybackRecoveryTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.DouyinRequestGateTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.BilibiliRoomFilterTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.NetworkOptimizationTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.HlsPlaybackTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.HlsPreferenceStoreTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.DouyuAccountTests]::RunAsync().GetAwaiter().GetResult()

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
$syntaxTrees = @{}
foreach ($path in $syntaxFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Compile source is missing: $path" }
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($path), $options, $path)
    $syntaxTrees[$path] = $tree
    $errors += @($tree.GetDiagnostics() | Where-Object { $_.Severity -eq 'Error' } | ForEach-Object ToString)
}
if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
Write-Output "PASS: C# 7.3 syntax and Compile paths ($($syntaxFiles.Count) source files)."

# Catch page-to-VM access errors that pure syntax tests miss, without WinRT build tools.
# Check declared members only; inherited members still require the full cloud compilation.
$inaccessible = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $syntaxFiles | Where-Object { [IO.Path]::GetFileName($_) -like 'LiveRoomVM*.cs' }) {
    foreach ($type in $syntaxTrees[$path].GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax] -and $_.Identifier.ValueText -eq 'LiveRoomVM'
    }) {
        foreach ($member in $type.Members) {
            $modifiers = @($member.Modifiers | ForEach-Object ValueText)
            if ($modifiers -contains 'public' -or ($modifiers -contains 'internal' -and $modifiers -notcontains 'private')) { continue }
            if ($member -is [Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax]) {
                foreach ($variable in $member.Declaration.Variables) { [void]$inaccessible.Add($variable.Identifier.ValueText) }
            } elseif ($member -is [Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax] -or
                $member -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax]) {
                [void]$inaccessible.Add($member.Identifier.ValueText)
            }
        }
    }
}
foreach ($path in $uwpSources | Where-Object { $_ -match '[\\/]Views[\\/]' }) {
    foreach ($access in $syntaxTrees[$path].GetRoot().DescendantNodes() | Where-Object {
        $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax] -and $_.Expression.ToString() -eq 'liveRoomVM'
    }) {
        if ($inaccessible.Contains($access.Name.Identifier.ValueText)) {
            throw "Page accesses a non-public LiveRoomVM member: $path - $access"
        }
    }
}
Write-Output 'PASS: playback pages do not access declared private/protected LiveRoomVM members.'
