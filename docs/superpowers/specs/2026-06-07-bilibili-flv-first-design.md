# Bilibili FLV-first stream selection design

## Goal

Move the proven Bilibili stream-selection strategy from `D:\D-Software\source\C1og-DTV` into `C1og-AllLive` with the smallest safe scope:

- Prefer Bilibili FLV streams when they are available.
- Use HLS only as fallback.
- Keep the existing UWP + FFmpegInteropX playback path.
- Do not add a local FLV proxy in this change.
- Bump the app version after code changes.
- Run static checks locally, then push to the remote branch so GitHub Actions performs the cloud build.

## Background

The latest `C1og-AllLive` log for Bilibili room `261095` shows that room detail loading and PlayInfo retrieval succeeded quickly, but playback entered the HLS `.m3u8` path and spent most of the delay in `FFmpegMediaSource.CreateFromUriAsync`, including a 12-second timeout and repeated line retries before playback succeeded.

The comparison project `C1og-DTV` loads the same room normally. Its effective Bilibili playback strategy is not only a request-parameter tweak:

- Rust backend calls `getRoomPlayInfo`.
- It requests `protocol=0,1`, `format=0,1,2`, `codec=0`, `platform=html5`, and `dolby=5`.
- It parses `stream -> format -> codec -> url_info`.
- It picks FLV first.
- HLS candidates are only verified and selected when no FLV stream is available.
- When FLV is selected, DTV normally serves it through a local `127.0.0.1:34719/live.flv` proxy and xgplayer plays it as FLV.

Only the stream-selection part is in scope for `C1og-AllLive`. The local proxy and browser/xgplayer behavior are explicitly out of scope.

## Non-goals

- Do not introduce a local HTTP proxy.
- Do not replace FFmpegInteropX or the UWP `MediaPlayerElement`.
- Do not change Bilibili danmaku behavior.
- Do not redesign the line-selection UI.
- Do not claim local UWP runtime playback verification from static checks.

## Architecture

The implementation should stay centered in `AllLive.Core\BiliBili.cs`.

Existing public shape remains:

- `GetPlayQuality()` / `GetPlayQualityNew()` continue to provide quality choices.
- `GetPlayUrls()` / `GetPlayUrlsNew()` continue to return `List<string>`.
- `LiveRoomVM.LoadPlayUrl()` continues to turn returned URLs into `PlayurlLine`.
- `LiveRoomPage.SetPlayer()` continues to build FFmpegInteropX media sources from the selected URL.

This keeps the change narrow and avoids changing the `ILiveSite` contract.

## Stream Request

For Bilibili PlayInfo requests, align the request shape with `C1og-DTV`:

- `protocol=0,1`
- `format=0,1,2`
- `codec=0`
- `platform=html5`
- `dolby=5`
- `qn=<selected quality>` when a quality was selected

The current header policy should remain:

- `User-Agent`
- `Referer: https://live.bilibili.com/`
- `Origin: https://live.bilibili.com`
- existing cookie/account headers
- `UseProxy = false`

The existing HEVC fallback rule remains conservative: first request AVC (`codec=0`), and only request `codec=0,1` when the AVC request produces no usable candidates.

## Candidate Parsing

`ParseBilibiliPlayUrlCandidates()` should keep richer metadata for ordering and logging:

- full URL
- `protocol_name`
- `format_name`
- `codec_name`
- whether the URL is FLV
- whether the URL is HLS
- whether the codec is HEVC
- URL `order`
- URL `score`
- whether the host is `mcdn`

FLV detection should use both `format_name == "flv"` and URL suffix/content checks. HLS detection should use both protocol and known HLS-style formats such as `ts`, `fmp4`, `mp4`, `m4s`, `m3u8`, plus `.m3u8` in the URL.

## Selection Order

Returned playback URLs should be ordered so the first line naturally follows the DTV strategy:

1. Non-HEVC before HEVC.
2. FLV before non-FLV.
3. Non-`mcdn` before `mcdn`.
4. Lower Bilibili `order` before higher `order`.
5. Higher Bilibili `score` before lower `score`.
6. Original API order as final tie-breaker.

If at least one FLV candidate exists, return the ordered candidates without doing extra HLS probing. This minimizes startup delay and avoids spending time on fallback paths that should not be used.

If no FLV candidate exists, HLS fallback may probe a small number of candidates:

- Prefer candidates containing `d1--cn`.
- Fall back to other HLS candidates only if preferred candidates fail.
- Keep the probe count bounded so entering a room does not become slower than the current behavior.

## Logging

Add targeted logs that are useful for future runtime diagnosis without printing secrets:

- total candidate count
- FLV candidate count
- HLS candidate count
- HEVC candidate count
- selected primary type: `FLV` or `HLS fallback`
- fallback reason when HLS is selected
- first selected host/path summary, without exposing full signed query in high-level summary logs

Existing detailed URL analysis in `LiveRoomPage` can still show per-attempt context during failures.

## Error Handling

If PlayInfo fails or returns no usable candidates, preserve the existing old-interface fallback behavior where practical and keep null-safe parsing. The implementation must not let a malformed PlayInfo response turn into a misleading `NullReferenceException`.

If FLV is selected but FFmpegInteropX fails to play it at runtime, existing line retry and next-line fallback should still be used. This design intentionally does not add FLV-specific runtime recovery in `LiveRoomPage`.

## Versioning

After code changes, bump the UWP package version in `AllLive.UWP\Package.appxmanifest`. The version bump is part of the implementation completion criteria because this repository is validated through GitHub Actions packaging rather than local UWP packaging.

## Verification

Local verification is limited because this machine does not have the full UWP build environment.

Required local checks:

- inspect the changed source paths after editing
- `git diff --check`
- targeted static/code-shape checks for the changed C# files
- confirm the package version diff
- confirm the final git status before commit and push

Not claimed locally:

- UWP package build
- app install
- live playback of room `261095`
- playback smoothness under real runtime conditions

After static checks pass, commit the code and push to the remote branch. GitHub Actions is the build validation path.

## Success Criteria

The implementation is considered ready for cloud validation when:

- `AllLive.Core\BiliBili.cs` follows FLV-first and HLS-fallback ordering.
- logs can distinguish FLV selection from HLS fallback.
- no local proxy or player-stack change is introduced.
- app version is bumped.
- local static checks pass.
- changes are committed and pushed for GitHub Actions.

Runtime success is confirmed only after the packaged app or subsequent app logs show that Bilibili room `261095` selects FLV when available and plays without the previous HLS `CreateFromUriAsync` startup delay.
