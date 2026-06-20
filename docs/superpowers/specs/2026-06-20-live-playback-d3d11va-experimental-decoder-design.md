# Live Playback Experimental D3D11VA Decoder Design

## Context

The playback diagnostics added in version 2.3.20.0 showed that some high-bitrate Douyin FLV streams can stall after the player has already entered `Playing`.

In the latest hard/soft decoder comparison, the same stream behaved much worse with `ForceSystemDecoder` than with `ForceFFmpegSoftwareDecoder`:

- Hard decoder: repeated `PositionDeltaMs=0` while `PlaybackState=Playing`.
- Software decoder: no suspicious stall samples, although a few samples still advanced slower than wall clock.
- Buffering and download progress stayed at 100%, and no media open/playback error was logged.

This points at the current system decoder path as a likely variable, but it does not prove that all hardware decoding is bad. We need one more manual decoder choice that can be tested independently.

## Scope

Add a fourth decoder option named experimental D3D11VA to the existing decoder selectors.

The feature is intentionally manual:

- Do not change the default decoder.
- Do not auto-switch on stalls.
- Do not remove the existing automatic, hard decoder, or software decoder modes.
- Keep the existing per-room decoder override behavior.

## UI

Add the option to all existing decoder combo boxes:

- Global settings page decoder selector.
- Live room overlay decoder selector.
- Live room settings pivot decoder selector.
- Xbox settings decoder selector.

The option text should be short because two of the selectors are compact:

- Global/live settings: `实验D3D11VA`
- Compact overlay: `D3D11VA`

## Playback Configuration

Use decoder index `3` for the experimental mode.

For index `3`, configure the FFmpeg media source as:

```text
VideoDecoderMode: Automatic
FFmpegOptions:
hwaccel=d3d11va
hwaccel_output_format=d3d11
```

The mode is experimental because FFmpegInteropX may still choose or wrap its internal decoder path. The runtime log must therefore remain the source of truth:

- `VideoDecoderMode`
- `hwaccel`
- `hwaccel_output_format`
- `Decoder Engine`
- playback samples

## Settings Compatibility

Existing setting values remain compatible:

- `0`: Automatic
- `1`: Force system decoder
- `2`: Force FFmpeg software decoder
- `3`: Experimental D3D11VA

Invalid values still fall back to the existing default, index `1`.

Settings backup import should accept values `0..3`.

## Verification

Local UWP build/runtime verification is not available on this machine.

Static verification should cover:

- `git diff --check`
- XAML/readback checks for the fourth decoder item
- source readback checks for index `3`, D3D11VA FFmpeg options, and logging text
- package manifest XML parse and version readback

Runtime verification requires installing the GitHub Actions build and comparing logs from the same room across:

- system hard decoder
- software decoder
- experimental D3D11VA
