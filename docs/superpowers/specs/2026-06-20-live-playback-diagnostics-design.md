# Live Playback Diagnostics Design

## Goal

Improve C1og-AllLive live-room playback logs so the next runtime log can separate these causes:

- Source stream or CDN instability.
- FFmpegInteropX media-source creation problems.
- Decoder backend instability, including hardware decode corruption.
- Runtime playback stalls where the player remains `Playing` but progress does not advance.
- Repeated buffering that does not currently reach the existing 8-second timeout log.

The immediate target is diagnostic coverage, not changing playback selection or forcing a different decoder backend.

## Current State

Playback is created in `AllLive.UWP/Views/LiveRoomPage.xaml.cs` through `FFmpegMediaSource.CreateFromUriAsync(url, config)`.

Current shared FFmpeg options:

- `rtsp_transport=tcp`
- `multiple_requests=1`
- `reconnect=1`
- `reconnect_at_eof=1`
- `reconnect_streamed=1`
- `reconnect_delay_max=2`

Current decoder modes:

- `Automatic` maps to `VideoDecoderMode.Automatic`.
- `硬解` maps to `VideoDecoderMode.ForceSystemDecoder`.
- `软解` maps to `VideoDecoderMode.ForceFFmpegSoftwareDecoder`.

The project references `FFmpegInteropX 2.0.0-pre7` and `FFmpegInteropX.FFmpegUWP 5.1.100`. The FFmpegUWP package contains FFmpeg DLLs built with `--enable-hwaccels --enable-d3d11va --disable-dxva2`, so D3D11VA is present in the native FFmpeg layer and DXVA2 is not.

## Diagnostic Approach

Add low-frequency structured debug logs around successful playback, not only failures.

### Playback Media Info

Log a `播放媒体信息` block after `MediaOpened` and the first `Playing` transition. Include:

- Site, room ID, title, quality, line name, URL host/path.
- Requested decoder mode and effective `VideoDecoderMode`.
- `CurrentVideoStream.CodecName`.
- Audio codec.
- Resolution.
- Video and audio bitrate.
- `CurrentVideoStream.DecoderEngine`.
- Package version and architecture if already available from diagnostics.

This converts the current UI-only `txtInfo` data into durable logs.

### Playback Timeline Sampling

Start a short sampler when playback enters `Playing`.

- Sample every 2 seconds.
- Stop after 30 seconds, when the page closes, or when a new media source attempt starts.
- Log each sample as `播放采样`.
- Include playback state, position, wall-clock delta, position delta, buffering progress, download progress, memory, and current URL host/path.
- Mark suspicious samples when state is `Playing` but position has not advanced enough for the elapsed wall-clock time.

This identifies cases where soft decode also stutters without triggering `MediaFailed`.

### Buffering Intervals

Add explicit buffering start/end logs:

- `BufferingStarted`: timestamp, current state, line, URL host/path.
- `BufferingEnded`: duration, current state, session snapshot.
- Existing 8-second timeout log remains.

This distinguishes short repeated buffering from a true decoder/render stall.

### FLV and URL Probe Context

Reuse the existing URL and FLV probe helpers, but keep them bounded.

- On startup diagnostics, log only URL host/path and media info.
- On short playback, failure, or severe stall, run the existing URL/FLV sample probe.
- Keep Range reads small and avoid logging signed query secrets.

### Error and Early-End Logs

Keep current failure logs for `MediaFailed`, media-source initialization failure, and final `直播加载失败`.

Extend early end or severe stall logs so they include:

- Last media info block.
- Last few playback samples summarized.
- URL/FLV probe result when available.

## D3D11VA and Hardware Decode Options

The native FFmpegUWP package includes D3D11VA support and disables DXVA2. Therefore an explicit `dxva2` mode should not be added.

An explicit experimental D3D11VA mode can be considered after diagnostics, but it should not be part of the first diagnostic-only change unless runtime logs prove the current `ForceSystemDecoder` path is not already using D3D11VA.

If added later, it should be exposed as an experimental decoder option and logged clearly, for example:

- `自动`: `VideoDecoderMode.Automatic`
- `系统硬解`: `VideoDecoderMode.ForceSystemDecoder`
- `FFmpeg软解`: `VideoDecoderMode.ForceFFmpegSoftwareDecoder`
- `实验D3D11VA`: only if FFmpegInteropX accepts `FFmpegOptions["hwaccel"] = "d3d11va"` without breaking UWP playback

The implementation must not assume that adding a CLI-style `hwaccel=d3d11va` option is equivalent to FFmpegInteropX's internal D3D11 path. It must be validated by logs showing the resulting `DecoderEngine`, codec, and playback behavior.

## Scope

In scope:

- `AllLive.UWP/Views/LiveRoomPage.xaml.cs` diagnostics.
- Small helper methods for redacted URL summaries, playback samples, and media info blocks.
- No secret-bearing full query logging beyond what the current code already logs.

Out of scope for this first pass:

- Changing stream ordering.
- Changing default decoder mode.
- Adding new UI choices for D3D11VA.
- Updating FFmpegInteropX packages.
- Claiming runtime playback is fixed without user-provided post-build logs.

## Verification

Available local verification:

- Static readback of edited code.
- XML parse if XAML is edited.
- `git diff --check`.
- C# build only where available locally; UWP package build may remain unavailable on this machine.

Runtime acceptance:

- A new app log for a problematic room shows `播放媒体信息`.
- It shows `播放采样` entries for the first 30 seconds.
- It logs buffering intervals if buffering occurs.
- If playback visually stutters, the log gives enough evidence to classify it as source/CDN buffering, decoder/render stall, or media-source failure.
