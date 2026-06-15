# Favorite refresh and Bilibili audience metric design

## Goal

Fix two narrow runtime behaviors in `C1og-AllLive`:

- Favorite auto refresh must not make a newly live room show a stale or cached live title.
- Bilibili live-room audience display must try the real viewer count before using heat/popularity.

The change should stay scoped to the existing favorite refresh and Bilibili room-detail paths. It should not redesign the favorite list UI, change the `ILiveSite` contract, or alter playback behavior.

## Current behavior

Favorite list:

- Manual refresh calls `ReloadAsync(..., loadRoomDetail: true)` and can load room detail titles.
- Auto refresh calls `RefreshLiveStatusOnly()`, which calls `ReloadAsync(..., loadRoomDetail: false)`.
- The status-only path currently preserves existing live titles and applies `MessageCenter.GetFavoriteLiveInfoCache()` before checking fresh live status.
- If a room was previously cached with a title, or had a title in the current list, a later status-only refresh can keep that title even when the refresh did not load current room detail.

Bilibili audience metric:

- `BiliBili.GetRoomDetail()` reads `getInfoByRoom.room_info.online` as popularity during the initial room-detail request.
- It then tries `GetInitialViewerCount()` for `ONLINE_RANK_COUNT`.
- The display layer already prefers `ViewerCount` over `VipCount` over `Popularity`, but the Bilibili data path still obtains heat before the real viewer-count attempt completes.

## Desired behavior

Favorite auto refresh:

- Continue using status-only refresh for automatic refreshes.
- A room that was offline before the refresh and becomes live during status-only refresh must show live status without a title.
- A room that was already live before the refresh may keep its existing title only if it is still live after the refresh.
- If a status-only refresh reports a room offline, clear its title.
- Cached live-room detail updates may still update the visible favorite page while the page is active, but the cache must not be applied as part of automatic status-only refresh.

Bilibili audience metric:

- For a live Bilibili room, attempt `ONLINE_RANK_COUNT` first.
- If the real viewer count succeeds, set `ViewerCount` and do not populate or display popularity from `room_info.online`.
- If the real viewer count fails or times out, read `room_info.online` as popularity fallback and show that value.
- For an offline Bilibili room, do not try websocket viewer count and do not need popularity fallback for the live-room display.

## Design

### Favorite refresh

Update `FavoriteVM.ReloadAsync()` so the status-only path does not apply `MessageCenter.GetFavoriteLiveInfoCache()`.

Replace the current broad title preservation with transition-aware preservation:

1. Capture the old state from `Items` before the status refresh mutates the new list.
2. For each new list item after `GetLiveStatus()` returns:
   - If `status == false`, set `LiveTitle` to empty.
   - If `status == true` and the previous state for the same favorite was also live, keep the previous title.
   - If `status == true` and the previous state was offline or missing, set `LiveTitle` to empty.
3. Keep manual refresh behavior unchanged: manual refresh with `loadRoomDetail: true` may still fetch detail and fill the title.

This preserves the useful part of `b686cdc` for rooms that remain live, while removing the stale-title case for offline-to-live transitions.

`MessageCenter.FavoriteLiveInfoUpdatedEvent` can remain subscribed by `FavoritePage`. When a live room detail page is open and sends a fresh title, the visible favorite list can still update immediately. The change only stops the automatic status refresh from preloading global cached titles into a newly refreshed list.

### Bilibili audience metric

Update `BiliBili.GetRoomDetail()` ordering:

1. Parse `room_info.live_status` and actual room id from `getInfoByRoom`.
2. If live, call `GetInitialViewerCount(actualRoomId)`.
3. If `viewerCount.HasValue`, return a `LiveRoomDetail` with:
   - `ViewerCount = viewerCount`
   - `ViewerCountSource = "websocket.ONLINE_RANK_COUNT"`
   - `Popularity = null`
   - `PopularitySource = null`
   - `Online = ToCompatibleOnline(viewerCount.Value)`
4. If viewer count fails, parse `room_info.online` and return it as popularity fallback.
5. If offline, return no viewer count and no popularity fallback unless existing callers require a nonzero compatible `Online`; the preferred value is `Online = 0`.

The initial `getInfoByRoom` call is still needed for status, title, room id, cover, anchor, and danmaku args. The change is about not reading or assigning the heat field until the real-count attempt has failed.

## Error handling

Favorite refresh:

- If `GetLiveStatus()` throws for one favorite, keep existing logging and avoid crashing the whole refresh.
- Failed status checks should not introduce or preserve titles for unknown current live state.
- Existing refresh-version cancellation remains unchanged.

Bilibili:

- `GetInitialViewerCount()` already returns `null` on timeout, websocket error, parse failure, or danmu-info failure.
- Only after that `null` result should `room_info.online` be parsed as popularity fallback.
- Logs should show whether the final initial metric came from `ONLINE_RANK_COUNT` or popularity fallback, without logging secrets.

## Verification

Local verification should include:

- Read back changed source around `FavoriteVM.ReloadAsync()`, title preservation, and Bilibili `GetRoomDetail()`.
- Run `git diff --check`.
- Run a targeted build/static check that is available locally, preferably `dotnet build AllLive.Core/AllLive.Core.csproj -c Debug` for the Bilibili core change.

Not claimed locally:

- UWP packaged-app runtime verification.
- Actual automatic favorite refresh reproduction.
- Actual Bilibili room websocket/runtime behavior in the UWP app.

Runtime confirmation should come from the app:

- A newly live room found only by automatic favorite refresh shows the live indicator without a title.
- Manual refresh or opening the live room fills the title.
- Bilibili live-room logs show real viewer count is attempted before popularity fallback.
