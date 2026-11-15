# HLS playback health (2.3.80)

## Read-ahead

HLS uses FFmpegInteropX 2.0.0's `config.General.ReadAheadBufferEnabled`,
`ReadAheadBufferSize` (Int64), and `ReadAheadBufferDuration`.
Read-ahead is enabled with upper bounds of **16 MiB and 8 seconds per stream**.
The limits apply to compressed stream packets, not total app memory. They are
not a required startup delay. FLV keeps its existing configuration.

The old reflection against MediaSourceConfig's top-level properties did not
enable read-ahead. The effective values are now included in the existing
debug configuration snapshot. UWP's BufferingProgress remains a separate
player status, not a measure of FFmpeg's queued seconds.

## Remembering stable Bilibili CDNs

After startup grace, the visible player must advance normally for 30 consecutive
seconds (0.90–1.15 times real time). Only then is its HLS host remembered, keyed
by canonical room ID and codec. LocalSettings stores only the host and UTC time;
it never stores the signed URL, path, cookie or line number. Entries expire in
7 days, with at most 128 entries, and are evicted oldest-first.

On the next URL load, exact-host matches are preferred, then another host in
the same CDN family, using only newly issued URLs. Existing quality and codec
selection still apply. An unavailable preference falls through to the normal
candidate order. A failing host loses its preference. An application restart
does not need to replay an expired stream URL.

## Bounded recovery

The health sampler uses a monotonic clock and one media-position read per
sample. It runs every 2 seconds throughout playback. Paused, hidden, non-1x,
delayed UI samples (dispatch >250 ms or interval >5 seconds), and the initial
12-second grace period do not qualify for recovery or learning.

After grace, recovery requires one of:

- At least 16 seconds of evidence in a roughly 20-second rolling window,
  progress below 85% of wall time, and at least 3 seconds of accumulated loss.
- Six consecutive seconds with progress below 20% of wall time.
- Three backward or unusually large (>1.5x) position jumps within 20 seconds.

For Bilibili HLS, select an untried host with the same codec, preferring a
different CDN family. If none exists, refresh the URLs for the same quality
and codec and rebuild through the existing recovery pipeline. There are at
most **two health-triggered recoveries per opened page**, separated by at least
60 seconds. Playing, URL renewal and automatic retries do not refill this
budget; manually refreshing/reopening the room creates a new page and budget.
Existing hard-failure recovery still has its six-attempt/two-minute limit and
native-open leases (one per page, two process-wide).

Manual line, decoder or quality selection cancels pending recovery. No health
action seeks blindly within a live stream, switches the decoder, or lowers
quality. When the health budget is exhausted, manual-switch tips remain.
General retry budgets reset only after 30 seconds of measured normal progress,
instead of accepting a Playing state with half-speed progress as stability.

## Diagnostics and validation

The existing log switch controls all output. First 30 seconds are sampled in
full; later, anomalous samples and one normal sample per minute are written.
`HealthWindowMs`, `HealthProgressMs`, `HealthDeficitMs`, `HealthEligible` and
`HealthReason` explain each decision. `[HlsHealth]` records preference use and
recovery targets using hostnames only. No additional playlist/segment probes,
bandwidth tests or network requests are made for diagnostics. The current
FFmpegInteropX public API does not expose per-segment download timings or raw
packet PTS; position jumps are evidence of a playback-clock discontinuity,
not proof of a particular CDN or decoder fault.

`.github/scripts/Test-PlaybackRecovery.ps1` exercises recorded slow-progress
and normal-progress sequences, transient stalls, invisible/delayed sampling,
timestamp jumps, signed URL renewal and bounded recovery, plus C# 7.3 syntax.
Full UWP compilation/packaging is validated by GitHub Actions. Installation
still needs a same-room playback check: verify read-ahead values, remembered
host selection on reopening, bounded switching under persistent slowdown, and
manual selection winning over an in-flight recovery.
