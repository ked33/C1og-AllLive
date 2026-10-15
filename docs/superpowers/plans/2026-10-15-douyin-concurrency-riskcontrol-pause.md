# 抖音关注并发与风控暂停倒计时实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> 本仓库工作流：直接提交 `master` 并推送触发 GitHub Actions 云构建（用户确认过，不使用 worktree）。

**Goal:** 新增「全站关注并发」「抖音关注并发」「444 暂停时长」「验证码暂停时长」四个设置项；命中 HTTP 444 或验证码中间页后暂停所有抖音直播间数据请求（默认 30 秒），并在所有已打开窗口常驻显示逐秒递减的倒计时 Toast。

**Architecture:** `AllLive.Core` 新增无锁静态闸门 `DouyinRequestGate`（暂停截止时间 + `Paused` 事件 + `WaitAsync`），接入 `Douyin.GetRoomDataApi/GetRoomDataHtml`；命中风控时 `GetRoomDetailByWebRidOnce` / `GetLiveStatusByWebRid` 不再回退 HTML。UWP 层：`FavoriteVM` 用按轮读取的可变信号量实现两级并发；`DouyinPauseToast` 订阅闸门事件，向每个已注册窗口的 `CoreDispatcher` 广播，由每窗口表现器驱动常驻 `MessageToast` 倒计时。

**Tech Stack:** C# 7.3（UWP 经典 csproj + netstandard2.0 Core）、PowerShell 7 静态检查（Roslyn 语法解析 + Add-Type 运行单元测试）、`dotnet build`（Core 全量类型检查）。

**设计文档:** `docs/superpowers/specs/2026-10-15-douyin-favorite-concurrency-riskcontrol-pause-design.md`（已提交 `247e130`）

---

## 文件结构

- Create: `AllLive.Core/DouyinRequestGate.cs` — 风控闸门（暂停状态、事件、等待、日志）
- Create: `.github/tests/DouyinRequestGateTests.cs` — 闸门单元测试（Add-Type 运行）
- Create: `AllLive.UWP/Helper/DouyinPauseToast.cs` — 多窗口倒计时常驻 Toast
- Modify: `AllLive.Core/Douyin.cs` — 4 处接入闸门
- Modify: `AllLive.UWP/Helper/SettingHelper.cs` — 4 个新设置 Key + 钳制读取方法
- Modify: `AllLive.UWP/ViewModels/FavoriteVM.cs` — 两级并发信号量
- Modify: `AllLive.UWP/Controls/MessageToast.xaml.cs` — 常驻模式 + 动态文字
- Modify: `AllLive.UWP/App.xaml.cs` — 初始化 + Provider + 注册主窗口
- Modify: `AllLive.UWP/Helper/MessageCenter.cs` — 注册直播窗口
- Modify: `AllLive.UWP/Views/SettingsPage.xaml` — 4 个设置卡片
- Modify: `AllLive.UWP/Views/SettingsPage.xaml.cs` — 设置读取/保存 + 备份还原
- Modify: `AllLive.UWP/AllLive.UWP.csproj` — 登记新 Compile 文件
- Modify: `.github/scripts/Test-PlaybackRecovery.ps1` — 加入闸门测试

---

### Task 0: 环境预检（不提交代码）

**Files:** 无改动。

- [ ] **Step 1: 确认工作区状态**

Run: `git status; git log --oneline -1`
Expected: `On branch master`，worktree clean，最新提交为计划文档提交（`docs: add douyin ... implementation plan` 或后续）。

- [ ] **Step 2: 基线运行 CI 静态检查脚本**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: 最后一行 `PASS: C# 7.3 syntax and Compile paths (... source files).`，无异常。

- [ ] **Step 3: 基线编译 Core**

Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Debug`
Expected: `Build succeeded`，0 Error（若 restore 下载包较慢属正常，首次可能 1-2 分钟）。

Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Release`
Expected: `Build succeeded`，0 Error。

若 Step 3 因环境原因（无网络/无缓存包）失败：记录输出，继续后续任务，最终验证以 Step 2 脚本 + 人工核对为准。

---

### Task 1: Core 风控闸门 + 单元测试（TDD）

**Files:**
- Create: `.github/tests/DouyinRequestGateTests.cs`
- Modify: `.github/scripts/Test-PlaybackRecovery.ps1`
- Create: `AllLive.Core/DouyinRequestGate.cs`

- [ ] **Step 1: 写失败的测试**

Create `.github/tests/DouyinRequestGateTests.cs`:

```csharp
using AllLive.Core;
using System;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class DouyinRequestGateTests
    {
        public static async Task<string> RunAsync()
        {
            await CheckPauseAndResumeAsync();
            await CheckExtensionNeverShortensAsync();
            CheckDefaultPauseSeconds();
            return "PASS: douyin request gate pause, resume, extension and default seconds.";
        }

        private static async Task CheckPauseAndResumeAsync()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = () => 2;
            DouyinRequestGate.CaptchaPauseSecondsProvider = () => 1;
            var pausedEvents = 0;
            EventHandler handler = (sender, args) => pausedEvents++;
            DouyinRequestGate.Paused += handler;
            try
            {
                Assert(!DouyinRequestGate.IsPaused, "Gate should start unpaused.");
                DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-444");
                Assert(DouyinRequestGate.IsPaused, "Gate should pause after a 444 report.");
                Assert(DouyinRequestGate.PauseRemainingSeconds >= 1, "Remaining seconds should be at least 1.");
                Assert(pausedEvents == 1, "Paused event should fire once.");

                var start = DateTimeOffset.UtcNow;
                await DouyinRequestGate.WaitAsync();
                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                Assert(!DouyinRequestGate.IsPaused, "Gate should be unpaused after waiting.");
                Assert(elapsed >= 1500, "WaitAsync returned before the pause elapsed.");
                Assert(elapsed <= 5000, "WaitAsync far exceeded the pause plus jitter bound.");

                var immediateStart = DateTimeOffset.UtcNow;
                await DouyinRequestGate.WaitAsync();
                var immediateElapsed = (DateTimeOffset.UtcNow - immediateStart).TotalMilliseconds;
                Assert(immediateElapsed < 100, "Unpaused WaitAsync should return immediately.");
            }
            finally
            {
                DouyinRequestGate.Paused -= handler;
            }
        }

        private static async Task CheckExtensionNeverShortensAsync()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = () => 3;
            DouyinRequestGate.CaptchaPauseSecondsProvider = () => 1;
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-444-long");
            await Task.Delay(100);
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Captcha, "test-captcha-short");
            Assert(DouyinRequestGate.PauseRemainingSeconds >= 2,
                "A shorter captcha pause must not shorten the active longer pause.");
            await DouyinRequestGate.WaitAsync();
            Assert(!DouyinRequestGate.IsPaused, "Gate should be unpaused after draining the extended pause.");
        }

        private static void CheckDefaultPauseSeconds()
        {
            DouyinRequestGate.BlockedPauseSecondsProvider = null;
            DouyinRequestGate.CaptchaPauseSecondsProvider = null;
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "test-default");
            Assert(DouyinRequestGate.PauseRemainingSeconds >= 28 && DouyinRequestGate.PauseRemainingSeconds <= 30,
                "The default pause should be 30 seconds.");
            // 不等待默认 30 秒暂停：这是最后一个用例，进程退出即结束（不再有后续请求）。
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
```

- [ ] **Step 2: 把闸门与测试加入 CI 检查脚本**

Modify `.github/scripts/Test-PlaybackRecovery.ps1`:

old:
```powershell
$sources = @(
    (Join-Path $repoRoot 'AllLive.UWP\Helper\PlaybackOperationGate.cs'),
    (Join-Path $repoRoot 'AllLive.Core\LiveStatusParser.cs'),
    (Join-Path $repoRoot '.github\tests\PlaybackRecoveryTests.cs')
)
```
new:
```powershell
$sources = @(
    (Join-Path $repoRoot 'AllLive.UWP\Helper\PlaybackOperationGate.cs'),
    (Join-Path $repoRoot 'AllLive.Core\LiveStatusParser.cs'),
    (Join-Path $repoRoot 'AllLive.Core\DouyinRequestGate.cs'),
    (Join-Path $repoRoot 'AllLive.Core\Helper\CoreDebug.cs'),
    (Join-Path $repoRoot '.github\tests\PlaybackRecoveryTests.cs'),
    (Join-Path $repoRoot '.github\tests\DouyinRequestGateTests.cs')
)
```

old:
```powershell
[AllLive.Tests.PlaybackRecoveryTests]::RunAsync().GetAwaiter().GetResult()
```
new:
```powershell
[AllLive.Tests.PlaybackRecoveryTests]::RunAsync().GetAwaiter().GetResult()
[AllLive.Tests.DouyinRequestGateTests]::RunAsync().GetAwaiter().GetResult()
```

- [ ] **Step 3: 运行脚本确认失败（红）**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: 失败 — `Add-Type` 报找不到 `AllLive.Core\DouyinRequestGate.cs`（Cannot find path）或找不到类型 `DouyinRequestGate`。

- [ ] **Step 3b: 提交测试（可先不提交，等绿后统一提交）**

统一在 Step 5 提交。

- [ ] **Step 4: 实现闸门**

Create `AllLive.Core/DouyinRequestGate.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using AllLive.Core.Helper;

namespace AllLive.Core
{
    /// <summary>抖音风控拦截类型。</summary>
    public enum DouyinBlockKind
    {
        /// <summary>HTTP 444 风控拦截。</summary>
        Http444,
        /// <summary>验证码中间页（HTTP 200 但返回验证码页面）。</summary>
        Captcha
    }

    /// <summary>
    /// 抖音请求闸门：任一抖音直播间数据请求命中风控（HTTP 444 或验证码中间页）后，
    /// 暂停所有抖音直播间数据请求若干秒（默认 30 秒，可由宿主配置），
    /// 暂停期间 WaitAsync 会等待，暂停结束后自动放行。
    /// </summary>
    public static class DouyinRequestGate
    {
        private const int DefaultPauseSeconds = 30;
        private const int MinPauseSeconds = 1;
        private const int MaxPauseSeconds = 600;
        private const int MaxWaitSliceMilliseconds = 500;
        private const int ResumeJitterMilliseconds = 800;

        private static readonly object _randomSync = new object();
        private static readonly Random _random = new Random();
        private static long _pausedUntilTicks;

        /// <summary>命中 HTTP 444 时的暂停秒数提供者（默认 30 秒），由宿主设置。</summary>
        public static Func<int> BlockedPauseSecondsProvider { get; set; }

        /// <summary>命中验证码中间页时的暂停秒数提供者（默认 30 秒），由宿主设置。</summary>
        public static Func<int> CaptchaPauseSecondsProvider { get; set; }

        /// <summary>暂停开始时触发；暂停被延长时会再次触发。</summary>
        public static event EventHandler Paused;

        /// <summary>当前是否处于风控暂停期。</summary>
        public static bool IsPaused
        {
            get { return PauseRemainingSeconds > 0; }
        }

        /// <summary>暂停剩余秒数（向上取整，未暂停时为 0）。</summary>
        public static int PauseRemainingSeconds
        {
            get
            {
                var remainingTicks = Volatile.Read(ref _pausedUntilTicks) - DateTimeOffset.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    return 0;
                }
                return (int)Math.Ceiling(remainingTicks / (double)TimeSpan.TicksPerSecond);
            }
        }

        /// <summary>记录一次风控命中并暂停后续请求（只会延长、不会缩短已生效的暂停）。</summary>
        public static void ReportBlocked(DouyinBlockKind kind, string reason)
        {
            var seconds = GetPauseSeconds(kind);
            var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (true)
            {
                var current = Volatile.Read(ref _pausedUntilTicks);
                if (until.Ticks <= current)
                {
                    break;
                }
                if (Interlocked.CompareExchange(ref _pausedUntilTicks, until.Ticks, current) == current)
                {
                    break;
                }
            }

            CoreDebug.Log(() => $"[Douyin] 命中风控(kind={(kind == DouyinBlockKind.Captcha ? "Captcha" : "Http444")}) 暂停抖音请求 {seconds}s reason={reason} until={until:HH:mm:ss}");

            var handler = Paused;
            if (handler != null)
            {
                try
                {
                    handler(null, EventArgs.Empty);
                }
                catch
                {
                }
            }
        }

        /// <summary>等待风控暂停结束（未暂停时立即返回）。</summary>
        public static async Task WaitAsync()
        {
            var waited = false;
            while (true)
            {
                var remainingTicks = Volatile.Read(ref _pausedUntilTicks) - DateTimeOffset.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    break;
                }
                if (!waited)
                {
                    waited = true;
                    var waitSeconds = (int)Math.Ceiling(remainingTicks / (double)TimeSpan.TicksPerSecond);
                    CoreDebug.Log(() => $"[Douyin] 风控暂停中，请求等待 {waitSeconds}s 后继续");
                }
                var sliceTicks = Math.Min(remainingTicks, MaxWaitSliceMilliseconds * TimeSpan.TicksPerMillisecond);
                var delayMilliseconds = (int)Math.Max(1L, sliceTicks / TimeSpan.TicksPerMillisecond);
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
            if (waited)
            {
                // 暂停结束后加一点抖动，避免并发请求在同一瞬间集中恢复再次触发风控。
                await Task.Delay(GetResumeJitterMilliseconds()).ConfigureAwait(false);
            }
        }

        private static int GetPauseSeconds(DouyinBlockKind kind)
        {
            var provider = kind == DouyinBlockKind.Captcha ? CaptchaPauseSecondsProvider : BlockedPauseSecondsProvider;
            var seconds = DefaultPauseSeconds;
            if (provider != null)
            {
                try
                {
                    seconds = provider();
                }
                catch
                {
                    seconds = DefaultPauseSeconds;
                }
            }
            if (seconds < MinPauseSeconds)
            {
                return MinPauseSeconds;
            }
            if (seconds > MaxPauseSeconds)
            {
                return MaxPauseSeconds;
            }
            return seconds;
        }

        private static int GetResumeJitterMilliseconds()
        {
            lock (_randomSync)
            {
                return _random.Next(0, ResumeJitterMilliseconds + 1);
            }
        }
    }
}
```

- [ ] **Step 5: 运行脚本确认通过（绿）**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: 无异常，最后一行 `PASS: C# 7.3 syntax and Compile paths (... source files).`（跳过闸门测试的独立输出；任一断言失败会抛异常）

- [ ] **Step 6: 提交**

```bash
git add AllLive.Core/DouyinRequestGate.cs .github/tests/DouyinRequestGateTests.cs .github/scripts/Test-PlaybackRecovery.ps1
git commit -m "feat(core): add douyin request gate with risk-control pause"
```

---

### Task 2: Douyin.cs 接入闸门（4 处改动）

**Files:**
- Modify: `AllLive.Core/Douyin.cs`（`GetRoomDataApi` / `GetRoomDataHtml` / `GetRoomDetailByWebRidOnce` / `GetLiveStatusByWebRid`）

- [ ] **Step 1: `GetRoomDataApi` 开头等待 + 444 上报**

old:
```csharp
        private async Task<JToken> GetRoomDataApi(string webRid)
        {
            var reqParams = new Dictionary<string, string> {
```
new:
```csharp
        private async Task<JToken> GetRoomDataApi(string webRid)
        {
            await DouyinRequestGate.WaitAsync();
            var reqParams = new Dictionary<string, string> {
```

old:
```csharp
                    if (statusCode == 444)
                    {
                        throw new DouyinRoomDataBlockedException("直播间详情API返回444");
                    }
```
new:
```csharp
                    if (statusCode == 444)
                    {
                        DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "GetRoomDataApi");
                        throw new DouyinRoomDataBlockedException("直播间详情API返回444");
                    }
```

- [ ] **Step 2: `GetRoomDataHtml` 开头等待 + 444/验证码上报**

old:
```csharp
        private async Task<JToken> GetRoomDataHtml(string webRid)
        {
            var dyCookie = await GetWebCookie(webRid);
```
new:
```csharp
        private async Task<JToken> GetRoomDataHtml(string webRid)
        {
            await DouyinRequestGate.WaitAsync();
            var dyCookie = await GetWebCookie(webRid);
```

old:
```csharp
                if (!response.IsSuccessStatusCode && statusCode == 444)
                {
                    CoreDebug.Log(() => "[Douyin] GetRoomDataHtml触发风控(444)");
                    throw new DouyinRoomDataBlockedException("直播间HTML请求返回444");
                }
                if (IsDouyinCaptchaPage(resp))
                {
                    CoreDebug.Log(() => "[Douyin] GetRoomDataHtml触发验证码中间页");
                    throw new DouyinRoomDataBlockedException("直播间HTML返回验证码中间页");
                }
```
new:
```csharp
                if (!response.IsSuccessStatusCode && statusCode == 444)
                {
                    CoreDebug.Log(() => "[Douyin] GetRoomDataHtml触发风控(444)");
                    DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "GetRoomDataHtml");
                    throw new DouyinRoomDataBlockedException("直播间HTML请求返回444");
                }
                if (IsDouyinCaptchaPage(resp))
                {
                    CoreDebug.Log(() => "[Douyin] GetRoomDataHtml触发验证码中间页");
                    DouyinRequestGate.ReportBlocked(DouyinBlockKind.Captcha, "GetRoomDataHtml");
                    throw new DouyinRoomDataBlockedException("直播间HTML返回验证码中间页");
                }
```

- [ ] **Step 3: `GetRoomDetailByWebRidOnce` 命中风控不回退 HTML**

old:
```csharp
        private async Task<LiveRoomDetail> GetRoomDetailByWebRidOnce(string webRid)
        {
            try
            {
                var result = await GetRoomDetailByWebRidApi(webRid);
                return result;
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Douyin] GetRoomDetailByWebRidApi失败 webRid={webRid} err={ex.GetType().FullName}: {ex.Message}");
            }
            return await GetRoomDetailByWebRidHtml(webRid);
        }
```
new:
```csharp
        private async Task<LiveRoomDetail> GetRoomDetailByWebRidOnce(string webRid)
        {
            try
            {
                var result = await GetRoomDetailByWebRidApi(webRid);
                return result;
            }
            catch (DouyinRoomDataBlockedException)
            {
                // 命中风控（444/验证码）时不再回退HTML：暂停期结束后由重试循环再次尝试，避免暂停期重复请求加重风控。
                throw;
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Douyin] GetRoomDetailByWebRidApi失败 webRid={webRid} err={ex.GetType().FullName}: {ex.Message}");
            }
            return await GetRoomDetailByWebRidHtml(webRid);
        }
```

- [ ] **Step 4: `GetLiveStatusByWebRid` 命中风控不回退 HTML**

old:
```csharp
        private async Task<bool> GetLiveStatusByWebRid(string webRid)
        {
            try
            {
                var data = await GetRoomDataApi(webRid);
                var roomData = data?["data"]?[0];
                return LiveStatusParser.IsLive(roomData?["status"], 2, 4);
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Douyin] GetLiveStatusByWebRidApi失败 webRid={webRid} err={ex.GetType().FullName}: {ex.Message}");
            }

            var roomDataHtml = await GetRoomDataHtml(webRid);
```
new:
```csharp
        private async Task<bool> GetLiveStatusByWebRid(string webRid)
        {
            try
            {
                var data = await GetRoomDataApi(webRid);
                var roomData = data?["data"]?[0];
                return LiveStatusParser.IsLive(roomData?["status"], 2, 4);
            }
            catch (DouyinRoomDataBlockedException)
            {
                // 命中风控时不回退HTML（暂停期请求会再次被拦），本轮直接失败，下一轮刷新重试。
                throw;
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Douyin] GetLiveStatusByWebRidApi失败 webRid={webRid} err={ex.GetType().FullName}: {ex.Message}");
            }

            var roomDataHtml = await GetRoomDataHtml(webRid);
```

- [ ] **Step 5: 验证**

Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Debug`
Expected: `Build succeeded`，0 Error。

Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Release`
Expected: `Build succeeded`，0 Error。

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: `PASS: C# 7.3 syntax ...`。

- [ ] **Step 6: 提交**

```bash
git add AllLive.Core/Douyin.cs
git commit -m "feat(core): pause douyin room data requests after risk-control hits"
```

---

### Task 3: SettingHelper 设置项 + FavoriteVM 两级并发

**Files:**
- Modify: `AllLive.UWP/Helper/SettingHelper.cs`
- Modify: `AllLive.UWP/ViewModels/FavoriteVM.cs`

- [ ] **Step 1: SettingHelper 新增 Key 与读取方法**

old:
```csharp
        /// <summary>
        /// 关注列表隐藏未开播
        /// </summary>
        public const string FAVORITE_HIDE_OFFLINE = "FavoriteHideOffline";
```
new:
```csharp
        /// <summary>
        /// 关注列表隐藏未开播
        /// </summary>
        public const string FAVORITE_HIDE_OFFLINE = "FavoriteHideOffline";

        /// <summary>
        /// 全站关注并发（关注列表检测开播状态的最大并发数，默认 6，范围 1-16）
        /// </summary>
        public const string FAVORITE_LIVE_STATUS_PARALLELISM = "FavoriteLiveStatusParallelism";
        /// <summary>
        /// 抖音关注并发（抖音检测的单独并发上限，默认 2，范围 1-16）
        /// </summary>
        public const string FAVORITE_DOUYIN_LIVE_STATUS_PARALLELISM = "FavoriteDouyinLiveStatusParallelism";
        /// <summary>
        /// 命中 HTTP 444 后暂停抖音请求的秒数（默认 30，范围 5-300）
        /// </summary>
        public const string DOUYIN_HTTP444_PAUSE_SECONDS = "DouyinHttp444PauseSeconds";
        /// <summary>
        /// 命中验证码中间页后暂停抖音请求的秒数（默认 30，范围 5-300）
        /// </summary>
        public const string DOUYIN_CAPTCHA_PAUSE_SECONDS = "DouyinCaptchaPauseSeconds";
```

old:
```csharp
        public static void SetValue<T>(string key, T value)
        {
            storageHelper.Save<T>(key, value);
        }
```
new:
```csharp
        public static void SetValue<T>(string key, T value)
        {
            storageHelper.Save<T>(key, value);
        }

        public static int GetFavoriteLiveStatusParallelism()
        {
            return ClampIntValue(GetValue<int>(FAVORITE_LIVE_STATUS_PARALLELISM, 6), 1, 16);
        }

        public static int GetFavoriteDouyinLiveStatusParallelism()
        {
            return ClampIntValue(GetValue<int>(FAVORITE_DOUYIN_LIVE_STATUS_PARALLELISM, 2), 1, 16);
        }

        public static int GetDouyinHttp444PauseSeconds()
        {
            return ClampIntValue(GetValue<int>(DOUYIN_HTTP444_PAUSE_SECONDS, 30), 5, 300);
        }

        public static int GetDouyinCaptchaPauseSeconds()
        {
            return ClampIntValue(GetValue<int>(DOUYIN_CAPTCHA_PAUSE_SECONDS, 30), 5, 300);
        }

        private static int ClampIntValue(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }
```

- [ ] **Step 2: FavoriteVM 字段替换（原常量信号量 → 可变两级信号量）**

old:
```csharp
        private const int LiveStatusParallelism = 6;
        private readonly SemaphoreSlim _liveStatusSemaphore = new SemaphoreSlim(LiveStatusParallelism, LiveStatusParallelism);
```
new:
```csharp
        private const int DefaultLiveStatusParallelism = 6;
        private const int DefaultDouyinLiveStatusParallelism = 2;
        private const string DouyinSiteName = "抖音直播";
        private readonly object _liveStatusSemaphoreSync = new object();
        private SemaphoreSlim _liveStatusSemaphore = new SemaphoreSlim(DefaultLiveStatusParallelism, DefaultLiveStatusParallelism);
        private SemaphoreSlim _douyinLiveStatusSemaphore = new SemaphoreSlim(DefaultDouyinLiveStatusParallelism, DefaultDouyinLiveStatusParallelism);
        private int _liveStatusSemaphoreSize = DefaultLiveStatusParallelism;
        private int _douyinLiveStatusSemaphoreSize = DefaultDouyinLiveStatusParallelism;
```

- [ ] **Step 3: 批量方法改为按轮取信号量快照**

old:
```csharp
        // 返回开播状态获取失败的关注项数量。
        private async Task<int> UpdateLiveStatusAsync(IList<FavoriteItem> items, int refreshVersion, bool loadRoomDetail)
        {
            var tasks = new List<Task<bool>>(items.Count);
            foreach (var item in items)
            {
                tasks.Add(UpdateLiveStatusAsync(item, refreshVersion, loadRoomDetail));
            }
            var results = await Task.WhenAll(tasks);
            return results.Count(failed => failed);
        }
```
new:
```csharp
        // 返回开播状态获取失败的关注项数量。
        private async Task<int> UpdateLiveStatusAsync(IList<FavoriteItem> items, int refreshVersion, bool loadRoomDetail)
        {
            GetLiveStatusSemaphores(out var liveStatusSemaphore, out var douyinLiveStatusSemaphore);
            var tasks = new List<Task<bool>>(items.Count);
            foreach (var item in items)
            {
                tasks.Add(UpdateLiveStatusAsync(item, refreshVersion, loadRoomDetail, liveStatusSemaphore, douyinLiveStatusSemaphore));
            }
            var results = await Task.WhenAll(tasks);
            return results.Count(failed => failed);
        }

        // 并发数设置即时生效：每轮刷新读取一次，容量变化时才整体替换信号量（旧信号量由在跑任务自行释放）。
        private void GetLiveStatusSemaphores(out SemaphoreSlim liveStatusSemaphore, out SemaphoreSlim douyinLiveStatusSemaphore)
        {
            var parallelism = SettingHelper.GetFavoriteLiveStatusParallelism();
            var douyinParallelism = SettingHelper.GetFavoriteDouyinLiveStatusParallelism();
            lock (_liveStatusSemaphoreSync)
            {
                if (parallelism != _liveStatusSemaphoreSize)
                {
                    _liveStatusSemaphore = new SemaphoreSlim(parallelism, parallelism);
                    _liveStatusSemaphoreSize = parallelism;
                }
                if (douyinParallelism != _douyinLiveStatusSemaphoreSize)
                {
                    _douyinLiveStatusSemaphore = new SemaphoreSlim(douyinParallelism, douyinParallelism);
                    _douyinLiveStatusSemaphoreSize = douyinParallelism;
                }
                liveStatusSemaphore = _liveStatusSemaphore;
                douyinLiveStatusSemaphore = _douyinLiveStatusSemaphore;
            }
        }
```

- [ ] **Step 4: 单项方法嵌套获取/倒序释放**

old:
```csharp
        private async Task<bool> UpdateLiveStatusAsync(FavoriteItem item, int refreshVersion, bool loadRoomDetail)
        {
            if (refreshVersion != _refreshVersion)
            {
                return false;
            }
            await _liveStatusSemaphore.WaitAsync();
            try
            {
                if (refreshVersion != _refreshVersion)
                {
                    return false;
                }
                var site = MainVM.Sites.FirstOrDefault(x => x.Name == item.SiteName);
```
new:
```csharp
        private async Task<bool> UpdateLiveStatusAsync(FavoriteItem item, int refreshVersion, bool loadRoomDetail, SemaphoreSlim liveStatusSemaphore, SemaphoreSlim douyinLiveStatusSemaphore)
        {
            if (refreshVersion != _refreshVersion)
            {
                return false;
            }
            var isDouyin = string.Equals(item.SiteName, DouyinSiteName, StringComparison.Ordinal);
            var douyinSlotTaken = false;
            await liveStatusSemaphore.WaitAsync();
            try
            {
                if (refreshVersion != _refreshVersion)
                {
                    return false;
                }
                if (isDouyin)
                {
                    // 抖音在「全站并发」之下再受「抖音并发」子上限约束（嵌套获取，释放顺序相反，无死锁）。
                    await douyinLiveStatusSemaphore.WaitAsync();
                    douyinSlotTaken = true;
                }
                var site = MainVM.Sites.FirstOrDefault(x => x.Name == item.SiteName);
```

old:
```csharp
            finally
            {
                _liveStatusSemaphore.Release();
            }
        }
```
new:
```csharp
            finally
            {
                if (douyinSlotTaken)
                {
                    douyinLiveStatusSemaphore.Release();
                }
                liveStatusSemaphore.Release();
            }
        }
```

- [ ] **Step 5: 校验无遗留引用 + 语法检查**

Run: `rg -n "_liveStatusSemaphore|LiveStatusParallelism" AllLive.UWP\ViewModels\FavoriteVM.cs`
Expected: 只剩新字段/新方法中的引用（`GetLiveStatusSemaphores` 内部与字段声明），没有裸的 `_liveStatusSemaphore.WaitAsync()`/`_liveStatusSemaphore.Release()` 旧调用。

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: `PASS: C# 7.3 syntax ...`。

- [ ] **Step 6: 提交**

```bash
git add AllLive.UWP/Helper/SettingHelper.cs AllLive.UWP/ViewModels/FavoriteVM.cs
git commit -m "feat(uwp): add favorite concurrency settings with douyin sub-limit"
```

---

### Task 4: MessageToast 常驻化 + DouyinPauseToast + 接线

**Files:**
- Modify: `AllLive.UWP/Controls/MessageToast.xaml.cs`
- Create: `AllLive.UWP/Helper/DouyinPauseToast.cs`
- Modify: `AllLive.UWP/AllLive.UWP.csproj`
- Modify: `AllLive.UWP/App.xaml.cs`
- Modify: `AllLive.UWP/Helper/MessageCenter.cs`

- [ ] **Step 1: MessageToast 增加常驻模式、动态文字、幂等关闭**

old:
```csharp
        private string m_TextBlockContent = "";
        private TimeSpan m_ShowTime;
```
new:
```csharp
        private string m_TextBlockContent = "";
        private TimeSpan m_ShowTime;
        private bool m_Persistent;
        private bool m_IsClosed;
```

old:
```csharp
        public void Show()
        {
            this.m_Popup.IsOpen = true;

        }
        public async void Close()
        {
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);

           // await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            this.m_Popup.IsOpen = false;
        }
```
new:
```csharp
        // 常驻模式：不自动关闭，调用方可 SetText 更新文字、Close 主动关闭（用于风控暂停倒计时）。
        public MessageToast(string content, bool persistent) : this()
        {
            if (m_TextBlockContent == null)
            {
                m_TextBlockContent = "";
            }
            this.m_TextBlockContent = content;
            this.m_ShowTime = TimeSpan.Zero;
            this.m_Persistent = persistent;
        }

        public void Show()
        {
            this.m_Popup.IsOpen = true;

        }

        public bool IsClosed
        {
            get { return m_IsClosed; }
        }

        // 动态更新文字（仅 UI 线程调用）。
        public void SetText(string text)
        {
            m_TextBlockContent = text ?? "";
            if (!m_IsClosed)
            {
                this.tbNotify.Text = m_TextBlockContent;
            }
        }

        public async void Close()
        {
            if (m_IsClosed)
            {
                return;
            }
            m_IsClosed = true;
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);

           // await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            this.m_Popup.IsOpen = false;
        }
```

old:
```csharp
            await AnimationBuilder.Create().Offset(to: new Vector2(0, -72), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: -72, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200), delay: TimeSpan.FromMilliseconds(m_ShowTime.TotalMilliseconds)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: m_ShowTime.TotalMilliseconds, easingType: EasingType.Default).StartAsync();
            this.m_Popup.IsOpen = false;
```
new:
```csharp
            await AnimationBuilder.Create().Offset(to: new Vector2(0, -72), duration: TimeSpan.FromMilliseconds(200)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: -72, duration: 200, delay: 0, easingType: EasingType.Default).StartAsync();
            if (m_Persistent)
            {
                // 常驻模式：保持显示，等待调用方 SetText / Close。
                return;
            }
            await AnimationBuilder.Create().Offset(to: new Vector2(0, (float)border.ActualHeight), duration: TimeSpan.FromMilliseconds(200), delay: TimeSpan.FromMilliseconds(m_ShowTime.TotalMilliseconds)).StartAsync(this);
            //await this.Offset(offsetX: 0, offsetY: (float)border.ActualHeight, duration: 200, delay: m_ShowTime.TotalMilliseconds, easingType: EasingType.Default).StartAsync();
            if (!m_IsClosed)
            {
                m_IsClosed = true;
                this.m_Popup.IsOpen = false;
            }
```

- [ ] **Step 2: 新建 DouyinPauseToast**

Create `AllLive.UWP/Helper/DouyinPauseToast.cs`:

```csharp
using AllLive.Core;
using AllLive.UWP.Controls;
using System;
using System.Collections.Concurrent;
using System.Threading;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace AllLive.UWP.Helper
{
    /// <summary>
    /// 抖音风控暂停倒计时 Toast：命中 444/验证码中间页后，在每个已注册窗口常驻显示剩余秒数，
    /// 暂停结束自动关闭。
    /// </summary>
    internal static class DouyinPauseToast
    {
        private static readonly ConcurrentDictionary<CoreDispatcher, Presenter> Presenters = new ConcurrentDictionary<CoreDispatcher, Presenter>();
        private static int _initialized;

        /// <summary>订阅风控闸门事件（幂等），在 App 构造函数中调用。</summary>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
            {
                return;
            }
            DouyinRequestGate.Paused += OnPaused;
        }

        /// <summary>注册当前视图：主窗口与每个直播窗口在创建时各调用一次。</summary>
        public static void RegisterCurrentView()
        {
            CoreDispatcher dispatcher;
            try
            {
                dispatcher = CoreApplication.GetCurrentView()?.Dispatcher;
            }
            catch
            {
                return;
            }
            if (dispatcher == null)
            {
                return;
            }
            Presenters.TryAdd(dispatcher, new Presenter());
        }

        private static void OnPaused(object sender, EventArgs e)
        {
            foreach (var pair in Presenters)
            {
                try
                {
                    if (pair.Key.TryRunAsync(CoreDispatcherPriority.Normal, pair.Value.Refresh) == null)
                    {
                        RemovePresenter(pair.Key);
                    }
                }
                catch
                {
                    // 调度失败说明窗口已关闭，移出注册表。
                    RemovePresenter(pair.Key);
                }
            }
        }

        private static void RemovePresenter(CoreDispatcher dispatcher)
        {
            Presenter ignored;
            Presenters.TryRemove(dispatcher, out ignored);
        }

        // 每个窗口一个倒计时表现器：1 秒一跳，剩余秒数直接读闸门状态（暂停延长时自动正确），归零关闭。
        private sealed class Presenter
        {
            private MessageToast _toast;
            private DispatcherTimer _timer;

            public void Refresh()
            {
                try
                {
                    if (_toast == null || _toast.IsClosed)
                    {
                        _toast = new MessageToast(BuildText(), true);
                        _toast.Show();
                    }
                    else
                    {
                        _toast.SetText(BuildText());
                    }
                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        _timer.Tick += Timer_Tick;
                    }
                    if (!_timer.IsEnabled)
                    {
                        _timer.Start();
                    }
                }
                catch
                {
                    _toast = null;
                }
            }

            private void Timer_Tick(object sender, object e)
            {
                try
                {
                    if (DouyinRequestGate.IsPaused)
                    {
                        if (_toast != null && !_toast.IsClosed)
                        {
                            _toast.SetText(BuildText());
                        }
                        return;
                    }
                }
                catch
                {
                }
                Stop();
            }

            private void Stop()
            {
                try
                {
                    if (_timer != null)
                    {
                        _timer.Stop();
                    }
                }
                catch
                {
                }
                try
                {
                    if (_toast != null)
                    {
                        _toast.Close();
                    }
                }
                catch
                {
                }
                _toast = null;
            }

            private static string BuildText()
            {
                return $"抖音请求已暂停，{DouyinRequestGate.PauseRemainingSeconds} 秒后恢复";
            }
        }
    }
}
```

- [ ] **Step 3: csproj 登记新文件**

Modify `AllLive.UWP/AllLive.UWP.csproj`:

old:
```xml
    <Compile Include="Helper\DouyinCookieStore.cs" />
```
new:
```xml
    <Compile Include="Helper\DouyinCookieStore.cs" />
    <Compile Include="Helper\DouyinPauseToast.cs" />
```

- [ ] **Step 4: App 初始化 + Provider + 注册主窗口**

Modify `AllLive.UWP/App.xaml.cs`:

old:
```csharp
            AllLive.Core.Helper.CoreDebug.Logger = message => LogHelper.Log(message, LogType.DEBUG);
```
new:
```csharp
            AllLive.Core.Helper.CoreDebug.Logger = message => LogHelper.Log(message, LogType.DEBUG);
            DouyinPauseToast.Initialize();
            AllLive.Core.DouyinRequestGate.BlockedPauseSecondsProvider = () => SettingHelper.GetDouyinHttp444PauseSeconds();
            AllLive.Core.DouyinRequestGate.CaptchaPauseSecondsProvider = () => SettingHelper.GetDouyinCaptchaPauseSeconds();
```

old:
```csharp
                LogHelper.LogDebug(() => "[Startup] Main window activated; elapsedMs=" + launchTiming?.ElapsedMilliseconds);
                LocalServiceManager.RegisterCurrentView();
```
new:
```csharp
                LogHelper.LogDebug(() => "[Startup] Main window activated; elapsedMs=" + launchTiming?.ElapsedMilliseconds);
                LocalServiceManager.RegisterCurrentView();
                DouyinPauseToast.RegisterCurrentView();
```

- [ ] **Step 5: MessageCenter 注册直播窗口**

Modify `AllLive.UWP/Helper/MessageCenter.cs`:

old:
```csharp
                    newViewId = view.Id;
                    LocalServiceManager.RegisterCurrentView();
```
new:
```csharp
                    newViewId = view.Id;
                    LocalServiceManager.RegisterCurrentView();
                    DouyinPauseToast.RegisterCurrentView();
```

- [ ] **Step 6: 校验**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: `PASS: C# 7.3 syntax and Compile paths (...)`（脚本会校验新 Compile 路径存在）。

人工核对（无法本地编译 UWP）：
- `DouyinPauseToast.cs` 的 using 与类型：`CoreDispatcher`/`CoreDispatcherPriority`（Windows.UI.Core）、`CoreApplication`（Windows.ApplicationModel.Core）、`DispatcherTimer`（Windows.UI.Xaml）、`MessageToast`（AllLive.UWP.Controls，已 using）。
- `ThemeHelper.Apply` 与 `MessageToast` 构造函数只在 UI 线程调用（表现器均在 `TryRunAsync` 内执行）。
- `App.xaml.cs` 中 `DouyinPauseToast`/`SettingHelper` 均在 `AllLive.UWP.Helper` 命名空间下，App 已 `using AllLive.UWP.Helper;`。

- [ ] **Step 7: 提交**

```bash
git add AllLive.UWP/Controls/MessageToast.xaml.cs AllLive.UWP/Helper/DouyinPauseToast.cs AllLive.UWP/AllLive.UWP.csproj AllLive.UWP/App.xaml.cs AllLive.UWP/Helper/MessageCenter.cs
git commit -m "feat(uwp): show persistent douyin pause countdown toast in all windows"
```

---

### Task 5: 设置页 UI + 备份还原

**Files:**
- Modify: `AllLive.UWP/Views/SettingsPage.xaml`
- Modify: `AllLive.UWP/Views/SettingsPage.xaml.cs`

- [ ] **Step 1: XAML 追加 4 个设置卡片**

Modify `AllLive.UWP/Views/SettingsPage.xaml`（「关注列表自动刷新」卡片之后、「图片降低亮度」之前插入）:

old:
```xml
                        <my:NumberBox
                            x:Name="numFavoriteRefreshInterval"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="120"
                            Minimum="1"
                            PlaceholderText="分钟"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsImageDim"
```
new:
```xml
                        <my:NumberBox
                            x:Name="numFavoriteRefreshInterval"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="120"
                            Minimum="1"
                            PlaceholderText="分钟"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsFavoriteLiveStatusParallelism"
                        Margin="0,8,0,0"
                        Padding="8,12"
                        Background="{ThemeResource LayerFillColorDefaultBrush}"
                        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                        BorderThickness="1"
                        CornerRadius="4">
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock>全站关注并发</TextBlock>
                            <TextBlock FontSize="12" Foreground="Gray" TextWrapping="Wrap">关注列表检测开播状态的最大并发数（默认 6）</TextBlock>
                        </StackPanel>
                        <my:NumberBox
                            x:Name="numFavoriteLiveStatusParallelism"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="16"
                            Minimum="1"
                            PlaceholderText="并发数"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsFavoriteDouyinLiveStatusParallelism"
                        Margin="0,8,0,0"
                        Padding="8,12"
                        Background="{ThemeResource LayerFillColorDefaultBrush}"
                        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                        BorderThickness="1"
                        CornerRadius="4">
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock>抖音关注并发</TextBlock>
                            <TextBlock FontSize="12" Foreground="Gray" TextWrapping="Wrap">抖音检测的单独并发上限，越小越不易触发风控（默认 2）</TextBlock>
                        </StackPanel>
                        <my:NumberBox
                            x:Name="numFavoriteDouyinLiveStatusParallelism"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="16"
                            Minimum="1"
                            PlaceholderText="并发数"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsDouyinHttp444PauseSeconds"
                        Margin="0,8,0,0"
                        Padding="8,12"
                        Background="{ThemeResource LayerFillColorDefaultBrush}"
                        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                        BorderThickness="1"
                        CornerRadius="4">
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock>444 暂停时长</TextBlock>
                            <TextBlock FontSize="12" Foreground="Gray" TextWrapping="Wrap">命中 HTTP 444 后暂停抖音请求的秒数（默认 30）</TextBlock>
                        </StackPanel>
                        <my:NumberBox
                            x:Name="numDouyinHttp444PauseSeconds"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="300"
                            Minimum="5"
                            PlaceholderText="秒"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsDouyinCaptchaPauseSeconds"
                        Margin="0,8,0,0"
                        Padding="8,12"
                        Background="{ThemeResource LayerFillColorDefaultBrush}"
                        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                        BorderThickness="1"
                        CornerRadius="4">
                        <StackPanel VerticalAlignment="Center">
                            <TextBlock>验证码暂停时长</TextBlock>
                            <TextBlock FontSize="12" Foreground="Gray" TextWrapping="Wrap">命中验证码中间页后暂停抖音请求的秒数（默认 30）</TextBlock>
                        </StackPanel>
                        <my:NumberBox
                            x:Name="numDouyinCaptchaPauseSeconds"
                            Width="100"
                            HorizontalAlignment="Right"
                            Maximum="300"
                            Minimum="5"
                            PlaceholderText="秒"
                            SpinButtonPlacementMode="Compact" />
                    </Grid>
                    <Grid
                        x:Name="SettingsImageDim"
```

- [ ] **Step 2: 代码后台：加载 + 保存（LoadUI 区域）**

Modify `AllLive.UWP/Views/SettingsPage.xaml.cs`:

old:
```csharp
                    SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, minutes);
                });
            });
            //视频解码
```
new:
```csharp
                    SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, minutes);
                });
            });

            //全站关注并发
            numFavoriteLiveStatusParallelism.Value = SettingHelper.GetFavoriteLiveStatusParallelism();
            numFavoriteLiveStatusParallelism.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numFavoriteLiveStatusParallelism.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }
                    SettingHelper.SetValue(SettingHelper.FAVORITE_LIVE_STATUS_PARALLELISM, ClampInt(Convert.ToInt32(args.NewValue), 1, 16));
                });
            });

            //抖音关注并发
            numFavoriteDouyinLiveStatusParallelism.Value = SettingHelper.GetFavoriteDouyinLiveStatusParallelism();
            numFavoriteDouyinLiveStatusParallelism.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numFavoriteDouyinLiveStatusParallelism.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }
                    SettingHelper.SetValue(SettingHelper.FAVORITE_DOUYIN_LIVE_STATUS_PARALLELISM, ClampInt(Convert.ToInt32(args.NewValue), 1, 16));
                });
            });

            //444 暂停时长（秒）
            numDouyinHttp444PauseSeconds.Value = SettingHelper.GetDouyinHttp444PauseSeconds();
            numDouyinHttp444PauseSeconds.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numDouyinHttp444PauseSeconds.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }
                    SettingHelper.SetValue(SettingHelper.DOUYIN_HTTP444_PAUSE_SECONDS, ClampInt(Convert.ToInt32(args.NewValue), 5, 300));
                });
            });

            //验证码暂停时长（秒）
            numDouyinCaptchaPauseSeconds.Value = SettingHelper.GetDouyinCaptchaPauseSeconds();
            numDouyinCaptchaPauseSeconds.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numDouyinCaptchaPauseSeconds.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }
                    SettingHelper.SetValue(SettingHelper.DOUYIN_CAPTCHA_PAUSE_SECONDS, ClampInt(Convert.ToInt32(args.NewValue), 5, 300));
                });
            });
            //视频解码
```

- [ ] **Step 3: 备份模型新增字段**

old:
```csharp
            public int? FavoriteAutoRefreshMinutes { get; set; }
```
new:
```csharp
            public int? FavoriteAutoRefreshMinutes { get; set; }
            public int? FavoriteLiveStatusParallelism { get; set; }
            public int? FavoriteDouyinLiveStatusParallelism { get; set; }
            public int? DouyinHttp444PauseSeconds { get; set; }
            public int? DouyinCaptchaPauseSeconds { get; set; }
```

- [ ] **Step 4: 导出备份包含新字段**

old:
```csharp
                FavoriteAutoRefreshMinutes = NormalizeFavoriteRefreshMinutes(null),
```
new:
```csharp
                FavoriteAutoRefreshMinutes = NormalizeFavoriteRefreshMinutes(null),
                FavoriteLiveStatusParallelism = SettingHelper.GetFavoriteLiveStatusParallelism(),
                FavoriteDouyinLiveStatusParallelism = SettingHelper.GetFavoriteDouyinLiveStatusParallelism(),
                DouyinHttp444PauseSeconds = SettingHelper.GetDouyinHttp444PauseSeconds(),
                DouyinCaptchaPauseSeconds = SettingHelper.GetDouyinCaptchaPauseSeconds(),
```

- [ ] **Step 5: 还原备份：写入 + 刷新 UI**

old:
```csharp
            var favoriteRefreshMinutes = NormalizeFavoriteRefreshMinutes(data.FavoriteAutoRefreshMinutes);
```
new:
```csharp
            var favoriteRefreshMinutes = NormalizeFavoriteRefreshMinutes(data.FavoriteAutoRefreshMinutes);
            var favoriteLiveStatusParallelism = ClampInt(data.FavoriteLiveStatusParallelism ?? SettingHelper.GetFavoriteLiveStatusParallelism(), 1, 16);
            var favoriteDouyinLiveStatusParallelism = ClampInt(data.FavoriteDouyinLiveStatusParallelism ?? SettingHelper.GetFavoriteDouyinLiveStatusParallelism(), 1, 16);
            var douyinHttp444PauseSeconds = ClampInt(data.DouyinHttp444PauseSeconds ?? SettingHelper.GetDouyinHttp444PauseSeconds(), 5, 300);
            var douyinCaptchaPauseSeconds = ClampInt(data.DouyinCaptchaPauseSeconds ?? SettingHelper.GetDouyinCaptchaPauseSeconds(), 5, 300);
```

old:
```csharp
            SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, favoriteRefreshMinutes);
```
new:
```csharp
            SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, favoriteRefreshMinutes);
            SettingHelper.SetValue(SettingHelper.FAVORITE_LIVE_STATUS_PARALLELISM, favoriteLiveStatusParallelism);
            SettingHelper.SetValue(SettingHelper.FAVORITE_DOUYIN_LIVE_STATUS_PARALLELISM, favoriteDouyinLiveStatusParallelism);
            SettingHelper.SetValue(SettingHelper.DOUYIN_HTTP444_PAUSE_SECONDS, douyinHttp444PauseSeconds);
            SettingHelper.SetValue(SettingHelper.DOUYIN_CAPTCHA_PAUSE_SECONDS, douyinCaptchaPauseSeconds);
```

old:
```csharp
            swMouseClosePage.IsOn = mouseBack;
            numFavoriteRefreshInterval.Value = favoriteRefreshMinutes;
```
new:
```csharp
            swMouseClosePage.IsOn = mouseBack;
            numFavoriteRefreshInterval.Value = favoriteRefreshMinutes;
            numFavoriteLiveStatusParallelism.Value = favoriteLiveStatusParallelism;
            numFavoriteDouyinLiveStatusParallelism.Value = favoriteDouyinLiveStatusParallelism;
            numDouyinHttp444PauseSeconds.Value = douyinHttp444PauseSeconds;
            numDouyinCaptchaPauseSeconds.Value = douyinCaptchaPauseSeconds;
```

- [ ] **Step 6: 校验**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: `PASS: C# 7.3 syntax ...`。

人工核对：
- XAML 4 个 `x:Name` 与代码后台引用完全一致：`numFavoriteLiveStatusParallelism`、`numFavoriteDouyinLiveStatusParallelism`、`numDouyinHttp444PauseSeconds`、`numDouyinCaptchaPauseSeconds`。
- `ClampInt(int,int,int)` 已存在于 SettingsPage.xaml.cs（约 L378），无需新增。
- `using Microsoft.UI.Xaml.Controls;`（NumberBox）已在文件顶部。

- [ ] **Step 7: 提交**

```bash
git add AllLive.UWP/Views/SettingsPage.xaml AllLive.UWP/Views/SettingsPage.xaml.cs
git commit -m "feat(uwp): expose douyin concurrency and pause settings in settings page"
```

---

### Task 6: 版本号升级、全量验证与推送

**Files:**
- Modify: `AllLive.UWP/Package.appxmanifest`
- Modify: `AllLive.UWP/Package.Desktop.appxmanifest`
- Modify: `AllLive.UWP/version.json`

- [ ] **Step 1: 升级三个版本文件（2.3.73 → 2.3.74）**

Modify `AllLive.UWP/Package.appxmanifest`:

old:
```xml
    Version="2.3.73.0" />
```
new:
```xml
    Version="2.3.74.0" />
```

Modify `AllLive.UWP/Package.Desktop.appxmanifest`:

old:
```xml
    Version="2.3.73.0" />
```
new:
```xml
    Version="2.3.74.0" />
```

Modify `AllLive.UWP/version.json`（整个文件替换，保持 Tab 缩进）:

```json
{
	"version": "2.3.74",
	"versionCode": 20374,
	"message": "1. 新增「全站关注并发」与「抖音关注并发」设置，抖音检测并发默认降为 2\n2. 新增「444 暂停时长」与「验证码暂停时长」设置，命中风控后暂停抖音请求（默认 30 秒）\n3. 命中风控暂停期间，所有窗口常驻显示倒计时，倒计时结束自动关闭",
	"url": "ms-windows-store://pdp?productid=9N1TWG2G84VD"
}
```

- [ ] **Step 2: 提交版本升级**

```bash
git add AllLive.UWP/Package.appxmanifest AllLive.UWP/Package.Desktop.appxmanifest AllLive.UWP/version.json
git commit -m "chore(release): bump version to 2.3.74"
```

- [ ] **Step 3: 运行 CI 静态检查（语法 + csproj 完整性 + 单元测试）**

Run: `pwsh -NoProfile -File .github/scripts/Test-PlaybackRecovery.ps1`
Expected: `PASS: C# 7.3 syntax and Compile paths (...)`，无异常。

- [ ] **Step 4: Core 全量编译（Debug + Release）**

Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Debug`
Run: `dotnet build AllLive.Core\AllLive.Core.csproj -c Release`
Expected: 均为 `Build succeeded`，0 Error。

- [ ] **Step 5: 差异与格式检查**

Run: `git diff --check HEAD~6..HEAD`（无输出）/ `git status`（clean）
Expected: 无空白错误，工作区干净。

- [ ] **Step 6: 复核关键不变量（人工清单）**

- `Douyin.cs`：`GetRoomDataApi`/`GetRoomDataHtml` 开头均有 `await DouyinRequestGate.WaitAsync();`；444/验证码上报齐全；两处 `catch (DouyinRoomDataBlockedException) { throw; }` 在泛型 catch 之前。
- `FavoriteVM.cs`：`douyinSlotTaken` 释放顺序正确；抖音并发嵌套在全站并发之内。
- `MessageToast.xaml.cs`：常驻模式 `return` 在自动关闭动画之前；`Close()` 幂等。
- `App.xaml.cs`：`DouyinPauseToast.Initialize()` 在 ctor；Provider 委托指向 SettingHelper 钳制读取方法。
- `AllLive.UWP.csproj`：`Helper\DouyinPauseToast.cs` 已登记。
- 全仓库无 C# 8+ 语法（无 `??=`、无 switch 表达式、无 `using var`、无目标类型 `new`）。
- 版本号三处一致：双清单 `2.3.74.0`、`version.json` `2.3.74` / `20374`。

- [ ] **Step 7: 推送触发云构建**

Run: `git push origin master`
Expected: push 成功，GitHub Actions 的 `Build UWP Application` 工作流在 master 上被触发。

---

## 设计文档对照检查（Self-Review）

- 4 个设置项（全站并发/抖音并发/444 秒数/验证码秒数，含默认与范围）→ Task 3、Task 5。
- 即时生效 → Task 3 `GetLiveStatusSemaphores` 每轮读取；Task 4 Provider 委托实时读取。
- 暂停语义（max 不缩短、500ms 等待、结束抖动、Provider 兜底）→ Task 1 实现 + 单元测试。
- 命中点与范围（API/HTML 两处等待与上报；跳过 HTML 回退）→ Task 2。
- 常驻倒计时 Toast（所有窗口、每秒递减、归零关闭、窗口关闭清理）→ Task 4。
- 备份/还原包含新设置 → Task 5。
- 版本号升级（双清单 2.3.74.0 + version.json 2.3.74/20374/更新说明）→ Task 6。
- 静态检查与推送 → Task 0、Task 6。
