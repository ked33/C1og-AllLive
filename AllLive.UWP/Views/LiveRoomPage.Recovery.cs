using AllLive.UWP.Helper;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace AllLive.UWP.Views
{
    public sealed partial class LiveRoomPage
    {
        private readonly PlaybackOperationGate mediaSourceGate = new PlaybackOperationGate();
        private readonly Random recoveryRandom = new Random(Guid.NewGuid().GetHashCode());
        private CancellationTokenSource mediaSourceWaitCts;
        private TaskCompletionSource<bool> recoveryPlaybackResult;
        private Stopwatch recoveryElapsed;
        private int recoveryGeneration;
        private Task<bool?> recoveryStatusTask;
        private string recoveryStatusKey;
        private Stopwatch recoveryStatusAge;
        private static readonly TimeSpan MediaSourceQueueTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RecoveryDurationLimit = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan RecoveryStatusInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RecoveryStatusTimeout = TimeSpan.FromSeconds(3);

        private void LogRecoveryEvent(string name, string reason = null, int? sourceVersion = null)
        {
            if (!LogHelper.Enabled) return;
            int pending;
            lock (mediaSourceCreateLock) pending = pendingMediaSourceCreateAttempts.Count;
            // Do not include signed URL paths/query strings, cookies or exception messages.
            LogHelper.LogDebug(() =>
                $"[PlaybackRecovery] event={name} generation={recoveryGeneration} " +
                $"source={sourceVersion ?? Volatile.Read(ref mediaSourceAttemptVersion)} " +
                $"attempt={streamReconnectAttempt}/{MaxStreamReconnectAttempts} " +
                $"pendingCreates={pending} processSlots={PlaybackOperationGate.ReservedProcessSlots} " +
                $"networkDown={isNetworkDown} stopped={playbackRecoveryStopped} reason={reason}");
        }

        private bool IsRecoveryCurrent(CancellationTokenSource owner)
        {
            return streamReconnectCts == owner && !owner.IsCancellationRequested &&
                !isPageClosing && !isNetworkDown && !playbackRecoveryStopped;
        }

        private TimeSpan RemainingRecoveryTime(TimeSpan maximum)
        {
            var remaining = RecoveryDurationLimit - (recoveryElapsed?.Elapsed ?? TimeSpan.Zero);
            if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
            return remaining < maximum ? remaining : maximum;
        }

        private bool TryScheduleStreamReconnect(string reason)
        {
            if (isPageClosing || isNetworkDown || playbackRecoveryStopped) return true;
            if (liveRoomVM?.detail == null) return false;
            CancelStablePlaybackReset();
            if (Interlocked.CompareExchange(ref streamReconnectInProgress, 1, 0) != 0)
            {
                // End the current observation window; only its owner schedules another attempt.
                recoveryPlaybackResult?.TrySetResult(false);
                return true;
            }
            if (recoveryElapsed == null) recoveryElapsed = Stopwatch.StartNew();
            else recoveryElapsed.Start();
            var owner = new CancellationTokenSource();
            streamReconnectCts = owner;
            LogRecoveryEvent("Scheduled", reason);
            _ = ReconnectStreamAsync(reason, owner);
            return true;
        }

        private async Task ReconnectStreamAsync(string reason, CancellationTokenSource owner)
        {
            var token = owner.Token;
            try
            {
                while (IsRecoveryCurrent(owner))
                {
                    if (streamReconnectAttempt >= MaxStreamReconnectAttempts ||
                        RemainingRecoveryTime(RecoveryDurationLimit) == TimeSpan.Zero)
                    {
                        StopPlaybackAfterFailure("自动恢复已停止，请点击刷新重试");
                        return;
                    }

                    ReleasePlaybackResources();
                    ReleaseDisplayRequestHold();
                    var baseDelay = StreamReconnectDelays[Math.Min(streamReconnectAttempt, StreamReconnectDelays.Length - 1)];
                    var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (0.8 + recoveryRandom.NextDouble() * 0.4));
                    streamReconnectAttempt++;
                    PlayerLoading.Visibility = Visibility.Visible;
                    PlayerLoadText.Text = $"直播流恢复中 ({streamReconnectAttempt}/{MaxStreamReconnectAttempts})";
                    LogRecoveryEvent("Backoff", reason);
                    await Task.Delay(RemainingRecoveryTime(delay), token);
                    if (!IsRecoveryCurrent(owner)) return;
                    if (RemainingRecoveryTime(RecoveryDurationLimit) == TimeSpan.Zero) continue;

                    var liveStatus = await CheckRecoveryLiveStatusAsync(token);
                    if (!IsRecoveryCurrent(owner)) return;
                    if (liveStatus == false)
                    {
                        StopPlaybackAfterFailure("主播已下播，请稍后刷新直播间");
                        return;
                    }
                    if (RemainingRecoveryTime(RecoveryDurationLimit) == TimeSpan.Zero) continue;
                    recoveryPlaybackResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var resultTask = recoveryPlaybackResult.Task;

                    if (!TryFallbackBilibiliCodec(reason))
                    {
                        var current = liveRoomVM.CurrentLine;
                        var lines = liveRoomVM.Lines;
                        var index = lines?.IndexOf(current) ?? -1;
                        var canSwitch = index >= 0 && index + 1 < lines.Count;
                        if (canSwitch && (streamReconnectAttempt > 1 || reason == "SourceFailed"))
                        {
                            liveRoomVM.CurrentLine = lines[index + 1];
                        }
                        else if (current != null && streamReconnectAttempt == 1 && reason != "SourceFailed")
                        {
                            QueueSetPlayer(current.Url, "Recovery");
                        }
                        else
                        {
                            var refresh = liveRoomVM.LoadPlayUrlAsync(token, refreshRoom: true);
                            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                var timeout = Task.Delay(RemainingRecoveryTime(TimeSpan.FromSeconds(20)), wait.Token);
                                if (await Task.WhenAny(refresh, timeout) != refresh)
                                {
                                    if (IsRecoveryCurrent(owner))
                                        StopPlaybackAfterFailure("刷新播放信息超时，请稍后重试");
                                    return;
                                }
                                wait.Cancel();
                                var refreshed = await refresh;
                                if (!IsRecoveryCurrent(owner)) return;
                                if (!refreshed) continue;
                            }
                        }
                    }

                    // Wait for an actual Playing/failure event, rather than periodically
                    // invalidating a slow open and starting another native operation.
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var timeout = Task.Delay(RemainingRecoveryTime(TimeSpan.FromSeconds(60)), wait.Token);
                        var completed = await Task.WhenAny(resultTask, timeout);
                        if (!IsRecoveryCurrent(owner)) return;
                        if (completed == resultTask)
                        {
                            wait.Cancel();
                            if (await resultTask) return;
                        }
                        else
                        {
                            LogRecoveryEvent("ObservationTimeout", reason);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsRecoveryCurrent(owner))
                {
                    if (LogHelper.Enabled) LogRecoveryEvent("RecoveryError", ex.GetType().Name);
                    StopPlaybackAfterFailure("播放恢复失败，请点击刷新重试");
                }
            }
            finally
            {
                if (streamReconnectCts == owner)
                {
                    streamReconnectCts = null;
                    recoveryPlaybackResult = null;
                    Interlocked.Exchange(ref streamReconnectInProgress, 0);
                }
                owner.Dispose();
            }
        }

        private async Task<bool?> CheckRecoveryLiveStatusAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var key = recoveryGeneration + "|" + liveRoomVM.SiteName + "|" + liveRoomVM.RoomID;
            if (recoveryStatusTask == null || (recoveryStatusTask.IsCompleted &&
                (recoveryStatusKey != key || recoveryStatusAge.Elapsed >= RecoveryStatusInterval)))
            {
                recoveryStatusKey = key;
                recoveryStatusAge = Stopwatch.StartNew();
                recoveryStatusTask = ObserveRecoveryLiveStatusAsync();
            }
            // An old room's query keeps its slot until the underlying request ends.
            if (recoveryStatusKey != key) return null;
            var query = recoveryStatusTask;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var timeout = Task.Delay(RemainingRecoveryTime(RecoveryStatusTimeout), wait.Token);
                if (await Task.WhenAny(query, timeout) != query)
                {
                    token.ThrowIfCancellationRequested();
                    LogRecoveryEvent("StatusUnknown", "Timeout");
                    return null;
                }
                wait.Cancel();
            }
            token.ThrowIfCancellationRequested();
            var status = await query;
            LogRecoveryEvent("StatusChecked", status.HasValue ? (status.Value ? "Live" : "Offline") : "Unknown");
            return status;
        }

        private async Task<bool?> ObserveRecoveryLiveStatusAsync()
        {
            try { return await liveRoomVM.ReadLiveStatusAsync(); }
            catch (Exception ex)
            {
                if (LogHelper.Enabled) LogRecoveryEvent("StatusUnknown", ex.GetType().Name);
                return null;
            }
        }

        private void PlayError()
        {
            TryScheduleStreamReconnect("SourceFailed");
        }
    }
}
