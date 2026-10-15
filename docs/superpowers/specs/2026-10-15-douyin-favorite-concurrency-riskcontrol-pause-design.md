# 抖音关注并发与风控暂停倒计时设计

## 目标

在 `C1og-AllLive` 上新增两类能力，缓解抖音（抖音直播）服务器端风控（HTTP 444 / 验证码中间页）导致的直播间无法加载问题：

1. 可配置的关注列表检测并发：新增「全站关注并发」与「抖音关注并发」两个设置项。
2. 命中一次风控（HTTP 444 或验证码中间页）后暂停所有抖音直播间数据请求若干秒（可配置，默认 30 秒），暂停期间在所有已打开窗口常驻显示逐秒递减的倒计时 Toast，暂停结束后自动关闭。

改动范围限定在关注列表并发控制、抖音房间数据请求路径和 Toast 提示，不修改 `ILiveSite` 契约、不改播放链路、不改弹幕与本地签名服务。

## 当前行为

- `FavoriteVM` 使用常量并发 `LiveStatusParallelism = 6` 检测关注列表开播状态（`_liveStatusSemaphore`，`ReloadAsync` → `UpdateLiveStatusAsync`，逐项调用 `site.LiveSite.GetLiveStatus`）。
- 抖音路径：`GetLiveStatusByWebRid` 与 `GetRoomDetailByWebRid`（4 次重试，延迟 5s/15s/30s）最终都落到 `GetRoomDataApi`（`live.douyin.com/webcast/room/web/enter/`）与 `GetRoomDataHtml`（`live.douyin.com/{webRid}`）。
- HTTP 444 与验证码中间页已在 `GetRoomDataApi` / `GetRoomDataHtml` 中识别并抛出 `DouyinRoomDataBlockedException`。
- 命中风控后没有任何全局节流：API 被 444 后同一轮立即回退请求 HTML，且多个并发项各自独立请求；日志显示这会让风控持续（同一时间段内多次 444）。
- `MessageToast` 是自解散的普通 Toast（显示若干秒后自动下移关闭），没有常驻/动态更新能力。

## 期望行为

1. 「全站关注并发」控制关注列表所有站点的最大并发（默认 6，范围 1–16）。
2. 「抖音关注并发」是嵌套在全站并发之下的抖音子上限（默认 2，范围 1–16）；抖音实际并发 = min(全站, 抖音)。
3. 命中 HTTP 444 后暂停抖音直播间数据请求 `DouyinHttp444PauseSeconds` 秒（默认 30，范围 5–300）。
4. 命中验证码中间页后暂停抖音直播间数据请求 `DouyinCaptchaPauseSeconds` 秒（默认 30，范围 5–300）。
5. 暂停期间新的抖音直播间数据请求等待暂停结束后才发出；暂停被再次命中时只会延长（取 当前截止时间 与 本次命中 now+时长 的较晚者），绝不会缩短已生效的暂停。
6. 暂停期间在所有已打开的窗口（主窗口与每个直播窗口）常驻显示「抖音请求已暂停，N 秒后恢复」，N 每秒递减；N 归零后自动关闭。
7. 所有设置即时生效（下一轮刷新 / 下一次命中即用新值），无需重启；并纳入设置备份/还原。

## 设计

### 1. 设置项（AllLive.UWP）

`Helper/SettingHelper.cs` 新增 Key 常量与带钳制的读取方法：

| Key 常量 | 存储键 | 默认 | 范围 | 读取方法 |
| --- | --- | --- | --- | --- |
| `FAVORITE_LIVE_STATUS_PARALLELISM` | `FavoriteLiveStatusParallelism` | 6 | 1–16 | `GetFavoriteLiveStatusParallelism()` |
| `FAVORITE_DOUYIN_LIVE_STATUS_PARALLELISM` | `FavoriteDouyinLiveStatusParallelism` | 2 | 1–16 | `GetFavoriteDouyinLiveStatusParallelism()` |
| `DOUYIN_HTTP444_PAUSE_SECONDS` | `DouyinHttp444PauseSeconds` | 30 | 5–300 | `GetDouyinHttp444PauseSeconds()` |
| `DOUYIN_CAPTCHA_PAUSE_SECONDS` | `DouyinCaptchaPauseSeconds` | 30 | 5–300 | `GetDouyinCaptchaPauseSeconds()` |

`Views/SettingsPage.xaml` 在「关注列表自动刷新」卡片下方追加 4 个 `my:NumberBox` 卡片（沿用现有卡片样式与 SpinButtonPlacementMode="Compact"）；`SettingsPage.xaml.cs` 按现有 `numFavoriteRefreshInterval` 模式接线（Loaded 后挂 ValueChanged、写入设置），并把 4 个字段加入 `SettingsBackupData` 与导出/还原流程。

### 2. FavoriteVM 并发改造（AllLive.UWP/ViewModels/FavoriteVM.cs）

- 删除常量并发；改为两个实例信号量 + 记录其容量，每轮 `UpdateLiveStatusAsync(items, ...)` 开始时调用 `EnsureLiveStatusSemaphores()`：读取设置，容量变化才整体替换 `SemaphoreSlim`（在锁内替换并返回快照）。被替换的旧信号量由仍在运行的旧任务自行释放，安全。
- 嵌套获取：每个关注项先 `await 全站信号量`，若 `item.SiteName == "抖音直播"` 再 `await 抖音信号量`；`finally` 中按相反顺序释放。抖音持有者必然同时持有全站槽位，因此无死锁。
- 同一轮内所有项使用同一组信号量快照，保证整轮并发一致。

### 3. 风控闸门（AllLive.Core/DouyinRequestGate.cs，新文件）

```csharp
public enum DouyinBlockKind { Http444, Captcha }

public static class DouyinRequestGate
{
    public static Func<int> BlockedPauseSecondsProvider { get; set; } // 默认 30，宿主（UWP）设置
    public static Func<int> CaptchaPauseSecondsProvider { get; set; } // 默认 30
    public static event EventHandler Paused;
    public static bool IsPaused { get; }
    public static int PauseRemainingSeconds { get; } // 向上取整，未暂停为 0
    public static void ReportBlocked(DouyinBlockKind kind, string reason);
    public static Task WaitAsync();
}
```

- `ReportBlocked`：`pausedUntilTicks = max(当前值, now + 对应秒数)`（CAS 循环，永不缩短），`CoreDebug` 记录 `[Douyin] 命中风控(kind=Http444|Captcha) 暂停抖音请求 Ns reason=... until=...`，随后触发 `Paused` 事件（监听方异常吞掉）。
- 秒数来自 Provider，Provider 为空/抛异常时回退 30；闸门内再钳制到 1–600。
- `WaitAsync`：未暂停立即返回；暂停中以 500ms 粒度轮询截止时间（暂停被延长时自动续等），首次等待时打一条日志；等待过暂停的请求在恢复后追加 0–800ms 随机抖动，避免并发请求同一瞬间集中恢复再次触发风控。
- 线程安全：`Volatile.Read` + `Interlocked.CompareExchange`，无锁。

插入点（`AllLive.Core/Douyin.cs`）：

- `GetRoomDataApi`：方法开头 `await DouyinRequestGate.WaitAsync()`；`statusCode == 444` 分支在抛异常前 `ReportBlocked(Http444, "GetRoomDataApi")`。
- `GetRoomDataHtml`：方法开头 `await DouyinRequestGate.WaitAsync()`；444 分支 `ReportBlocked(Http444, "GetRoomDataHtml")`；验证码分支 `ReportBlocked(Captcha, "GetRoomDataHtml")`。
- 跳过 HTML 回退：
  - `GetRoomDetailByWebRidOnce`：捕获 `DouyinRoomDataBlockedException` 时直接 `throw`（不再回退 HTML）；其他异常保持原有回退行为。
  - `GetLiveStatusByWebRid`：同样在 `DouyinRoomDataBlockedException` 时直接抛出。
  - 原因：命中风控后 HTML 几乎必被拦（见日志），立即回退只会多等一个完整暂停期并加重风控。房间详情由原有 4 次重试循环按暂停节奏重试；关注状态由下一轮刷新重试。
- 范围：只拦 `GetRoomDataApi` / `GetRoomDataHtml`（直播间详情与开播状态的全部路径）；分类页、搜索、`webcast.amemv.com` 长房间号路径、弹幕 WebSocket、本地签名服务（127.0.0.1:8788）不在拦截范围。

### 4. 常驻倒计时 Toast（AllLive.UWP）

`Controls/MessageToast.xaml.cs` 扩展（不新增 XAML）：

- 新增常驻构造函数 `MessageToast(string content, bool persistent)`；`m_Persistent = true` 时 `NotifyPopup_Loaded` 完成入场动画后不再自动关闭。
- 新增 `SetText(string)`（供倒计时每秒更新文字）与 `IsClosed` 属性；`Close()` 幂等（重复调用直接返回），自动关闭路径同样置 `IsClosed`。

`Helper/DouyinPauseToast.cs`（新文件，需在 `AllLive.UWP.csproj` 登记 `<Compile Include="Helper\DouyinPauseToast.cs" />`）：

- `Initialize()`（App 构造函数调用，幂等）：订阅 `DouyinRequestGate.Paused`。
- `RegisterCurrentView()`：把当前视图的 `CoreDispatcher` 加入注册表（每个窗口一个表现器）。调用点：
  - `App.OnLaunched`（主窗口，紧邻 `LocalServiceManager.RegisterCurrentView()`）；
  - `MessageCenter.OpenLiveRoomWindowAsync`（每个直播窗口的 `newView.Dispatcher.RunAsync` 内，紧邻 `LocalServiceManager.RegisterCurrentView()`）。
- `Paused` 事件（线程池线程触发）→ 遍历注册表，对每个窗口 `TryRunAsync(CoreDispatcherPriority.Normal, ...)` 调其表现器 `Refresh()`；调度失败（窗口已关闭）则把该窗口移出注册表；表现器内部全部 `try/catch`，Toast 异常不影响请求链路。
- 表现器（每窗口一个）：首次 `Refresh()` 创建常驻 `MessageToast` 并启动 `DispatcherTimer`（1 秒）；每次 Tick 读取 `DouyinRequestGate.PauseRemainingSeconds` 更新文字「抖音请求已暂停，N 秒后恢复」；剩余为 0 时停止计时并关闭 Toast。倒计时直接读闸门状态，因此暂停被延长/重置时显示自动正确。
- `App` 构造函数同时设置闸门 Provider：
  `DouyinRequestGate.BlockedPauseSecondsProvider = () => SettingHelper.GetDouyinHttp444PauseSeconds();`
  `DouyinRequestGate.CaptchaPauseSecondsProvider = () => SettingHelper.GetDouyinCaptchaPauseSeconds();`
  Provider 每次命中时实时读取设置，无需额外的变更通知。

## 错误处理与边界

- 暂停延长：新命中取 `max(当前截止, now+时长)`，绝不缩短已有暂停。
- Provider 缺失/异常：回退默认 30 秒；闸门内钳制 1–600 秒。
- `Paused` 事件监听方异常：`ReportBlocked` 内吞掉，不影响请求链路。
- 跨线程调度失败（窗口已关）：移出注册表并忽略；表现器内部异常全部吞掉。
- 并发刷新轮次：信号量按轮替换，旧任务继续使用并释放旧实例；不会出现信号量泄漏或重复释放。
- 值域钳制在设置层（读取方法）与闸门层双重保证；UI NumberBox 的 Min/Max 仅作输入引导。
- 暂停期间被取消的刷新轮次：底层请求仍会等暂停结束后发出一次（无 CancellationToken 传递，保持最小改动），结果按现有版本号机制丢弃。

## 验证

本地可用 pwsh 7.6 + .NET 8 SDK，因此：

- 运行 CI 自带静态检查 `.github/scripts/Test-PlaybackRecovery.ps1`（Roslyn C# 7.3 语法解析全部 UWP `Compile` 项与 Core 源码、csproj 引用完整性、回放守卫测试）。
- `dotnet build AllLive.Core/AllLive.Core.csproj`（netstandard2.0 全量类型检查，覆盖新 `DouyinRequestGate.cs` 与 `Douyin.cs` 改动）。
- UWP 侧逐文件人工核对 API/类型/`x:Name` 一致性，并确认新文件已登记进 `AllLive.UWP.csproj`。
- 全部通过后提交并 push `master`，由 GitHub Actions 云构建做最终编译验证。

不在本地声称：

- UWP 打包应用的运行时验证。
- 实际风控 444 命中的端到端复现。
- 多窗口 Toast 的实际渲染效果。

运行时确认（云构建安装后）：

- 设置页出现 4 个新设置项，修改后下一轮关注刷新即生效。
- 命中 444 或验证码页后，所有已打开窗口出现常驻倒计时 Toast，每秒递减，30 秒后自动关闭。
- 暂停期间日志出现 `[Douyin] 命中风控(...) 暂停抖音请求 30s` 与 `[Douyin] 风控暂停中，请求等待 Ns 后继续`，且暂停期内没有新的抖音请求发出。
