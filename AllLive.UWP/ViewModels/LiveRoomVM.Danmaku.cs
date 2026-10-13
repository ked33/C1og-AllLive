using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.UWP.Helper;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace AllLive.UWP.ViewModels
{
    public partial class LiveRoomVM
    {
        private readonly SemaphoreSlim danmakuStartGate = new SemaphoreSlim(1, 1);
        private Task danmakuStopTask = Task.CompletedTask;
        private CancellationTokenSource danmakuStableCts;
        private ILiveDanmaku closedDanmaku;
        private bool liveExtrasPaused;

        public void PauseLiveExtras()
        {
            liveExtrasPaused = true;
            CancelDanmakuReconnect();
            CancelDanmakuStableReset();
            Interlocked.Exchange(ref deferredLiveExtrasStartState, 0);
            _ = CleanupOldDanmakuAsync();
        }

        public void StartDeferredLiveExtras()
        {
            if (!isActive || Site == null || detail == null || !detail.Status || danmakuArgs == null) return;
            liveExtrasPaused = false;
            if (LiveDanmaku != null || danmakuReconnectAttempt >= MaxDanmakuReconnectAttempts ||
                Interlocked.CompareExchange(ref deferredLiveExtrasStartState, 1, 0) != 0) return;

            LoadSuperChat();
            _ = StartDeferredDanmakuAsync();
            _ = LoadDeferredAudienceMetricsAsync();
        }

        private async Task StartDeferredDanmakuAsync()
        {
            var version = roomLoadVersion;
            try
            {
                if (await ConnectDanmakuAsync(CancellationToken.None, version)) return;
            }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => $"[DanmakuRecovery] event=StartFailed error={ex.GetType().Name}");
            }
            if (IsRoomLoadCurrent(version) && !liveExtrasPaused)
                StartDanmakuReconnect(useInitialFailureMessage: true);
        }

        private async Task<bool> ConnectDanmakuAsync(CancellationToken token, int version)
        {
            await danmakuStartGate.WaitAsync(token);
            ILiveDanmaku candidate = null;
            var connected = false;
            try
            {
                if (!CanConnectDanmaku(token, version)) return false;
                await CleanupOldDanmakuAsync();
                if (!CanConnectDanmaku(token, version)) return false;
                candidate = Site.GetDanmaku();
                if (candidate == null) return false;
                candidate.NewMessage += LiveDanmaku_NewMessage;
                candidate.OnClose += LiveDanmaku_OnClose;
                LiveDanmaku = candidate;
                closedDanmaku = null;
                await candidate.Start(danmakuArgs);
                if (!CanConnectDanmaku(token, version) || !ReferenceEquals(LiveDanmaku, candidate) ||
                    ReferenceEquals(closedDanmaku, candidate)) return false;

                connected = true;
                _ = ResetDanmakuAfterStableConnectionAsync(candidate, version);
                LogHelper.LogDebug(() => $"[DanmakuRecovery] event=Connected attempt={danmakuReconnectAttempt}");
                return true;
            }
            finally
            {
                try
                {
                    if (!connected && candidate != null)
                    {
                        DetachDanmaku(candidate);
                        await danmakuStopTask;
                        // Start may finish after a previous Stop. Reap any late socket/timer.
                        await StopDanmakuSafelyAsync(candidate);
                    }
                }
                finally { danmakuStartGate.Release(); }
            }
        }

        private bool CanConnectDanmaku(CancellationToken token, int version)
        {
            return IsRoomLoadCurrent(version) && !liveExtrasPaused && !token.IsCancellationRequested && Site != null;
        }

        private async void LiveDanmaku_OnClose(object sender, string reason)
        {
            var dispatcher = Dispatcher;
            if (!isActive || dispatcher == null) return;
            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!isActive || liveExtrasPaused || !ReferenceEquals(sender, LiveDanmaku) ||
                        ReferenceEquals(sender, closedDanmaku)) return;
                    closedDanmaku = LiveDanmaku;
                    CancelDanmakuStableReset();
                    Messages.Add(new LiveMessage { Type = LiveMessageType.Chat, UserName = "系统", Message = "弹幕连接已断开" });
                    LogHelper.LogDebug(() => $"[DanmakuRecovery] event=Closed attempt={danmakuReconnectAttempt}");
                    StartDanmakuReconnect(useInitialFailureMessage: false);
                });
            }
            catch (Exception) when (!isActive || Dispatcher == null) { }
        }

        private void StartDanmakuReconnect(bool useInitialFailureMessage)
        {
            if (!isActive || liveExtrasPaused || Dispatcher == null ||
                Interlocked.CompareExchange(ref reconnectInProgress, 1, 0) != 0) return;
            var owner = new CancellationTokenSource();
            danmakuReconnectCts = owner;
            _ = ReconnectDanmakuAsync(useInitialFailureMessage, owner, roomLoadVersion);
        }

        private async Task ReconnectDanmakuAsync(bool initialFailure, CancellationTokenSource owner, int version)
        {
            var token = owner.Token;
            try
            {
                while (CanConnectDanmaku(token, version) && danmakuReconnectCts == owner)
                {
                    if (danmakuReconnectAttempt >= MaxDanmakuReconnectAttempts)
                    {
                        await CleanupOldDanmakuAsync();
                        if (!CanConnectDanmaku(token, version)) return;
                        Messages.Add(new LiveMessage { Type = LiveMessageType.Chat, UserName = "系统", Message = "弹幕重连已达上限，请手动刷新" });
                        RaiseReconnectStatus("弹幕", false, danmakuReconnectAttempt, MaxDanmakuReconnectAttempts, "弹幕重连失败");
                        return;
                    }

                    var delay = DanmakuReconnectDelays[Math.Min(danmakuReconnectAttempt, DanmakuReconnectDelays.Length - 1)];
                    danmakuReconnectAttempt++;
                    RaiseReconnectStatus("弹幕", true, danmakuReconnectAttempt, MaxDanmakuReconnectAttempts,
                        $"弹幕{(initialFailure ? "启动失败" : "连接异常")}，{delay.TotalSeconds:F0}s 后重试 ({danmakuReconnectAttempt}/{MaxDanmakuReconnectAttempts})");
                    LogHelper.LogDebug(() => $"[DanmakuRecovery] event=Backoff attempt={danmakuReconnectAttempt}");
                    await Task.Delay(delay, token);
                    if (!CanConnectDanmaku(token, version)) return;
                    try
                    {
                        if (!await ConnectDanmakuAsync(token, version)) continue;
                        if (!CanConnectDanmaku(token, version)) return;
                        Messages.Add(new LiveMessage { Type = LiveMessageType.Chat, UserName = "系统", Message = "弹幕已重新连接" });
                        RaiseReconnectStatus("弹幕", false, danmakuReconnectAttempt, MaxDanmakuReconnectAttempts, "弹幕已重连");
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogHelper.LogDebug(() => $"[DanmakuRecovery] event=ConnectFailed attempt={danmakuReconnectAttempt} error={ex.GetType().Name}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (danmakuReconnectCts == owner)
                {
                    danmakuReconnectCts = null;
                    Interlocked.Exchange(ref reconnectInProgress, 0);
                }
                owner.Dispose();
            }
        }

        private async Task ResetDanmakuAfterStableConnectionAsync(ILiveDanmaku candidate, int version)
        {
            CancelDanmakuStableReset();
            var owner = new CancellationTokenSource();
            danmakuStableCts = owner;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), owner.Token);
                if (CanConnectDanmaku(owner.Token, version) && danmakuStableCts == owner &&
                    ReferenceEquals(LiveDanmaku, candidate) && !ReferenceEquals(closedDanmaku, candidate))
                {
                    danmakuReconnectAttempt = 0;
                    LogHelper.LogDebug(() => "[DanmakuRecovery] event=StableBudgetReset");
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (danmakuStableCts == owner) danmakuStableCts = null;
                owner.Dispose();
            }
        }

        private void CancelDanmakuStableReset()
        {
            var previous = danmakuStableCts;
            danmakuStableCts = null;
            previous?.Cancel();
        }

        private void CancelDanmakuReconnect()
        {
            var previous = danmakuReconnectCts;
            danmakuReconnectCts = null;
            previous?.Cancel();
            Interlocked.Exchange(ref reconnectInProgress, 0);
        }

        private void DetachDanmaku(ILiveDanmaku candidate)
        {
            candidate.NewMessage -= LiveDanmaku_NewMessage;
            candidate.OnClose -= LiveDanmaku_OnClose;
            if (ReferenceEquals(LiveDanmaku, candidate)) LiveDanmaku = null;
        }

        private Task CleanupOldDanmakuAsync()
        {
            var old = LiveDanmaku;
            if (old == null) return danmakuStopTask;
            CancelDanmakuStableReset();
            DetachDanmaku(old);
            danmakuStopTask = Task.WhenAll(danmakuStopTask, StopDanmakuSafelyAsync(old));
            return danmakuStopTask;
        }

        private static async Task StopDanmakuSafelyAsync(ILiveDanmaku candidate)
        {
            try { await candidate.Stop(); }
            catch { }
        }
    }
}
