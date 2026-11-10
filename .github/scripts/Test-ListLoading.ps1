#requires -Version 7.0
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$relativeSources = @(
    'AllLive.UWP/Helper/FavoriteRefreshGate.cs',
    'AllLive.UWP/Helper/PagedRequestState.cs',
    'AllLive.UWP/Helper/LiveMessageBuffer.cs',
    '.github/tests/LiveMessageBufferTests.cs',
    'AllLive.UWP/ViewModels/SearchVM.cs',
    'AllLive.UWP/ViewModels/RecomendVM.cs',
    'AllLive.UWP/ViewModels/CategoryDetailVM.cs',
    'AllLive.UWP/ViewModels/Base/BaseViewModel.cs',
    'AllLive.UWP/ViewModels/Base/RelayCommand.cs',
    'AllLive.Core/Interface/ILiveSite.cs',
    'AllLive.Core/Interface/IDeferredAudienceMetricSite.cs',
    'AllLive.Core/Twitch.cs',
    'AllLive.Core/Danmaku/TwitchDanmaku.cs',
    'AllLive.Core/LiveStatusParser.cs',
    'AllLive.Core/Helper/CoreDebug.cs',
    '.github/tests/TwitchRequestLifetimeTests.cs',
    'AllLive.Core/Interface/ILiveDanmaku.cs',
    'AllLive.Core/Helper/DanmakuColor.cs',
    '.github/tests/ListLoadingTests.cs'
)
$sources = @($relativeSources | ForEach-Object { Join-Path $repoRoot $_ })
$sources += Get-ChildItem -LiteralPath (Join-Path $repoRoot 'AllLive.Core/Models') -Filter '*.cs' -File | ForEach-Object FullName
$references = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' -File | ForEach-Object FullName)
$references += Join-Path $PSHOME 'Newtonsoft.Json.dll'
Add-Type -Path $sources -ReferencedAssemblies $references -CompilerOptions '/langversion:7.3', '/nowarn:0067,1701'
[AllLive.Tests.ListLoadingTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.LiveMessageBufferTests]::Run()
[AllLive.Tests.TwitchRequestLifetimeTests]::RunAsync().GetAwaiter().GetResult()
