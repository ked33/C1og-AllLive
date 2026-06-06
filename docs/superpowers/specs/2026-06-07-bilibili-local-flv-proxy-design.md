# Bilibili local FLV proxy design

## Goal

Bring `C1og-AllLive` closer to the proven `C1og-DTV` Bilibili playback path by adding a local FLV proxy for Bilibili FLV streams:

- If Bilibili PlayInfo returns FLV, register the upstream FLV URL with a local proxy service.
- Return a local URL such as `http://127.0.0.1:8789/api/bilibili/live.flv` to the UWP player.
- Let FFmpegInteropX play the local FLV proxy URL instead of the signed upstream URL.
- If PlayInfo returns no FLV, keep HLS fallback and log that local FLV proxy is unavailable for this room/request.
- Do not replace the UWP player stack.
- Bump the app version after implementation.
- Run local static checks and push to `origin/master`; GitHub Actions remains the UWP package build validation path.

## Evidence From Latest Runtime Log

The latest log for room `261095` on app version `2.3.11.0` shows the previous FLV-first request change worked structurally but did not solve the room:

- PlayInfo produced `total=6 flv=0 hls=6 hevc=2`.
- The app logged `未获得FLV，使用HLS fallback`.
- Playback then retried HLS `.m3u8` lines and repeatedly failed with `FFmpegMediaSource.CreateFromUriAsync 超时 12s`.
- The final result was `直播加载失败`.

This means local proxy work must be scoped carefully: a proxy can improve FLV playback when FLV is available, but it cannot create FLV when Bilibili only returns HLS to the current request.

## Current Repository Constraints

`C1og-AllLive` currently has:

- UWP app project: `AllLive.UWP`.
- Core stream parsing project: `AllLive.Core`.
- Existing ASP.NET Core helper project: `AllLive.SignService`, listening on port `8788`.
- No current `runFullTrust` or Desktop Bridge startup wiring in `Package.appxmanifest`.
- `AllLive.SignService` is not part of `AllLive.sln`.
- The GitHub Actions workflow builds the UWP project; it does not package or start `AllLive.SignService`.

Because of that, the first implementation should not depend on automatic FullTrust startup or on bundling the proxy into the UWP package. The first step should create a local service that can be started externally, then make UWP/Core use it opportunistically.

## Recommended Approach

Extend `AllLive.SignService` into a small local media helper service and reserve a separate proxy port:

- Keep existing signing APIs on port `8788`.
- Add a Bilibili FLV proxy listener on port `8789`, or allow the same executable to expose both signing and proxy endpoints while keeping Bilibili proxy URLs on `8789`.
- Add endpoints:
  - `GET /health`
  - `POST /api/bilibili/stream`
  - `GET /api/bilibili/live.flv`

This reuses the existing .NET 8 service project and avoids adding a new dependency-heavy service. It also avoids changing the UWP package model in the first iteration.

## Endpoint Contract

`POST /api/bilibili/stream`

Request body:

```json
{
  "url": "https://example.bilivideo.com/live-bvc/.../live_xxx.flv?...",
  "referer": "https://live.bilibili.com/",
  "userAgent": "Mozilla/5.0 ...",
  "cookie": ""
}
```

Response body:

```json
{
  "code": 0,
  "data": {
    "url": "http://127.0.0.1:8789/api/bilibili/live.flv"
  },
  "msg": ""
}
```

The service should store only the latest registered Bilibili FLV upstream URL in memory. It should not persist URLs to disk.

`GET /api/bilibili/live.flv`

The proxy should:

- Return `404` if no upstream URL has been registered.
- Use `HttpClient` with `UseProxy = false`.
- Request the upstream with:
  - `User-Agent`
  - `Referer: https://live.bilibili.com/`
  - `Accept: video/x-flv,application/octet-stream,*/*`
  - `Range: bytes=0-`
  - `Connection: keep-alive`
- Stream the upstream response body to the caller.
- Return `Content-Type: video/x-flv`.
- Disable caching with `Cache-Control: no-store`.
- Log only a redacted host/path summary, not the full signed query.

## AllLive.Core Integration

Add a Bilibili local proxy client in `AllLive.Core`, close to `BiliBili.cs` or under `AllLive.Core\Helper`.

When `GetPlayUrlsNew()` selects candidates:

1. If the selected ordered candidates include FLV:
   - Try registering the first FLV upstream URL with the local proxy.
   - If registration succeeds, put the returned local proxy URL first.
   - Append remaining FLV upstream URLs and fallback candidates after it.
   - Log `选择本地FLV代理`.
2. If registration fails:
   - Keep the real upstream FLV URL first.
   - Log `本地FLV代理不可用，回退上游FLV`.
3. If no FLV exists:
   - Do not call proxy registration.
   - Keep current HLS fallback.
   - Log `未获得FLV，无法使用本地代理`.

The existing `List<string>` return type stays unchanged.

## UWP Playback Integration

No player-stack replacement is planned.

`LiveRoomPage.SetPlayer()` continues to receive a URL and call `FFmpegMediaSource.CreateFromUriAsync`. The only behavioral change is that FLV-first can now pass a local URL:

```text
http://127.0.0.1:8789/api/bilibili/live.flv
```

The existing `.m3u8` 12-second create timeout should remain limited to Bilibili HLS URLs. Local FLV URLs should not be treated as Bilibili HLS.

## Settings And Defaults

First iteration defaults:

- Proxy enabled by default.
- Proxy base URL default: `http://127.0.0.1:8789`.
- If the local proxy is unreachable, the app falls back automatically and logs the fallback.

To keep the first implementation narrow, adding a full Settings UI for the proxy is optional. A constant or `SettingHelper` key is enough if the code needs a configurable base URL.

## Service Startup

The first implementation does not add automatic UWP startup of the proxy service.

Expected first-run behavior:

- User or helper script starts the local service.
- UWP detects it by trying to register FLV or health-checking the endpoint.
- If the service is not running, playback still attempts upstream FLV/HLS fallback.

Future work can add Desktop Bridge / FullTrust startup after the proxy path is proven by runtime logs.

## Security And Privacy

- Do not write signed Bilibili URLs or cookies to disk.
- Do not print complete signed query strings in high-level logs.
- Bind the proxy to `127.0.0.1` only.
- Keep only the latest upstream URL in memory.
- Do not expose proxy endpoints on `0.0.0.0` for the media proxy.
- Avoid adding broad capabilities to UWP in the first iteration.

## Error Handling

Proxy registration failure:

- Log the failure type and message.
- Continue with upstream FLV URL.

Proxy stream failure:

- Return an appropriate HTTP status to the player.
- Log upstream status or connection error with redacted URL summary.
- Let existing player retry/next-line logic handle playback failure.

No FLV available:

- Log explicitly that the local proxy cannot be used.
- Continue HLS fallback.

## Versioning

Implementation should bump `AllLive.UWP\Package.appxmanifest` from `2.3.11.0` to the next version, expected `2.3.12.0`.

If `AllLive.SignService` behavior changes, keep the service project versionless unless the repository already has a versioning pattern for it.

## Verification

Local checks:

- `git diff --check`
- `dotnet build AllLive.Core/AllLive.Core.csproj -c Debug`
- `dotnet build AllLive.SignService/AllLive.SignService.csproj -c Debug`
- targeted source readback for:
  - proxy endpoints
  - FLV registration path
  - fallback logs
  - manifest version bump

Not claimed locally:

- UWP package build
- app install
- automatic service startup from UWP
- runtime playback success

After local static checks pass, commit and push to `origin/master`. GitHub Actions remains the package build validation path. Runtime proof must come from the app log after testing.

## Success Criteria

The implementation is ready for runtime validation when:

- A local service exposes `POST /api/bilibili/stream` and `GET /api/bilibili/live.flv`.
- `AllLive.Core` registers FLV candidates with the proxy when FLV exists.
- The first returned Bilibili playback URL is local proxy FLV when registration succeeds.
- If FLV is absent, logs say that local proxy cannot be used and HLS fallback continues.
- Full signed URLs are not written to high-level logs.
- UWP version is bumped.
- Local static checks pass.
- Changes are pushed for GitHub Actions.

Runtime success is confirmed only when app logs show either:

- `选择本地FLV代理` followed by successful playback, or
- `未获得FLV，无法使用本地代理`, proving the remaining failure is Bilibili returning HLS-only data rather than proxy integration.
