using AllLive.UWP.Models;
using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using AllLive.Core.Models;
using AllLive.UWP.Helper;
using Microsoft.UI.Xaml.Controls;
using NSDanmaku.Model;
using Windows.UI.ViewManagement;
using Windows.UI.Popups;
using Windows.System.Display;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Graphics.Imaging;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel;
using System.Diagnostics;
using Newtonsoft.Json;
using Windows.UI.Core;
using FFmpegInteropX;
using Windows.Media.Playback;
using System.Text;
using Windows.Web.Http;
using Windows.Web.Http.Headers;
using Windows.Networking.Connectivity;
using Windows.System.Profile;
using Windows.Storage.Streams;
using System.ComponentModel;
// https://go.microsoft.com/fwlink/?LinkId=234238 上介绍了“空白页”项模板

namespace AllLive.UWP.Views
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class LiveRoomPage : Page
    {
        readonly LiveRoomVM liveRoomVM;
        readonly SettingVM settingVM;

        FFmpegInteropX.FFmpegMediaSource interopMSS;
        readonly MediaPlayer mediaPlayer;

        DisplayRequest dispRequest;
        PageArgs pageArgs;
        //当前处于小窗
        private bool isMini = false;
        DispatcherTimer timer_focus;
        DispatcherTimer controlTimer;
        private string lastConfigSnapshot;
        private string lastUrlAnalysis;
        private string lastProbeSnapshot;
        private string lastMediaInfoSnapshot;
        private string diagnosticsSnapshot;
        private Task diagnosticsSnapshotTask;
        private DateTimeOffset? lastHuyaRefreshUtc;
        private static readonly TimeSpan HuyaRefreshCooldown = TimeSpan.FromSeconds(30);
        private MediaPlaybackState? lastPlaybackState;
        private DateTimeOffset? lastMediaOpenedUtc;
        private DateTimeOffset? lastPlaybackStartUtc;
        private string lastPlaybackUrl;
        private string currentLineRetryUrl;
        private int currentLineRetryCount;
        private static readonly TimeSpan EarlyEndThreshold = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan CurrentLineRetryDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan BilibiliRetryWindow = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan BilibiliCreateSourceTimeout = TimeSpan.FromSeconds(12);
        private const string BilibiliPlaybackUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

        private System.Threading.CancellationTokenSource streamReconnectCts;
        private int streamReconnectAttempt;
        private int streamReconnectLevel;
        private int streamReconnectInProgress;
        private bool isNetworkDown;
        private DispatcherTimer bufferingTimer;
        private readonly object setPlayerRequestLock = new object();
        private bool isPageClosing;
        private bool liveRoomCleaned;
        private bool mediaPlayerEventsDetached;
        private bool liveRoomWindowRegistered;
        private bool controlEventsDetached;
        private bool windowContentCleared;
        private bool setPlayerWorkerRunning;
        private string activeSetPlayerUrl;
        private DateTimeOffset activeSetPlayerUtc;
        private string pendingSetPlayerUrl;
        private DateTimeOffset pendingSetPlayerUtc;
        private int mediaSourceAttemptVersion;
        private System.Threading.CancellationTokenSource playbackSamplingCts;
        private bool playbackStallProbeStarted;
        private DateTimeOffset? currentBufferingStartedUtc;
        private readonly Queue<string> playbackSampleHistory = new Queue<string>();
        private static readonly TimeSpan[] StreamReconnectDelays = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(15),
        };
        private const int MaxStreamReconnectAttempts = 6;
        private static readonly TimeSpan BufferingTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan DuplicateSetPlayerWindow = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PlaybackSampleInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan PlaybackSampleDuration = TimeSpan.FromSeconds(30);
        private const int DefaultVideoDecoderIndex = 1;
        private const int MaxPlaybackSampleHistory = 8;
        private bool updatingDecoderSelection;

        private sealed class PlaybackSampleResult
        {
            public string Text { get; set; }
            public bool Suspicious { get; set; }
            public string Url { get; set; }
        }

        public LiveRoomPage()
        {
            this.InitializeComponent();

            settingVM = new SettingVM();
            liveRoomVM = new LiveRoomVM(settingVM);
            liveRoomVM.Dispatcher = this.Dispatcher;
            dispRequest = new DisplayRequest();

            Window.Current.CoreWindow.KeyDown += CoreWindow_KeyDown;
          
            liveRoomVM.ChangedPlayUrl += LiveRoomVM_ChangedPlayUrl;
            liveRoomVM.PropertyChanged += LiveRoomVM_PropertyChanged;
            liveRoomVM.AddDanmaku += LiveRoomVM_AddDanmaku;
            liveRoomVM.ReconnectStatusChanged += LiveRoomVM_ReconnectStatusChanged;
            //每过2秒就设置焦点
            timer_focus = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(2) };
            timer_focus.Tick += Timer_focus_Tick;
            controlTimer = new DispatcherTimer() { Interval = TimeSpan.FromSeconds(1) };
            controlTimer.Tick += ControlTimer_Tick;
            mediaPlayer = new MediaPlayer();
            mediaPlayer.RealTimePlayback = true;
            mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            mediaPlayer.PlaybackSession.BufferingStarted += PlaybackSession_BufferingStarted;
            mediaPlayer.PlaybackSession.BufferingProgressChanged += PlaybackSession_BufferingProgressChanged;
            mediaPlayer.PlaybackSession.BufferingEnded += PlaybackSession_BufferingEnded;
            mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            bufferingTimer = new DispatcherTimer() { Interval = BufferingTimeout };
            bufferingTimer.Tick += BufferingTimer_Tick;
            NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
            isNetworkDown = !IsNetworkConnected();

            timer_focus.Start();
            controlTimer.Start();
            if (Utils.IsXbox && SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0) == 0)
            {
                XBoxControl.Visibility = Visibility.Visible;
                StandardControl.Visibility = Visibility.Collapsed;
            }
            else
            {
                XBoxControl.Visibility = Visibility.Collapsed;
                ClearXboxSettingBind();
                StandardControl.Visibility = Visibility.Visible;
            }

            // 新窗口打开，调整UI
            if (SettingHelper.GetValue(SettingHelper.NEW_WINDOW_LIVEROOM, true))
            {
                ApplicationView.GetForCurrentView().Consolidated += LiveRoomPage_Consolidated;
                TitleBar.Visibility = Visibility.Visible;
                // 自定义标题栏
                CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = true;
                Window.Current.SetTitleBar(TitleBar);
                SetTitleBarColor();
            }

        }

        private async void LiveRoomPage_Consolidated(ApplicationView sender, ApplicationViewConsolidatedEventArgs args)
        {
            await CleanupLiveRoomPageAsync(clearWindowContent: true);
            // 关闭窗口
            CoreWindow.GetForCurrentThread().Close();
        }

     
        private void SetTitleBarColor()
        {
            var settingTheme = SettingHelper.GetValue<int>(SettingHelper.THEME, 0);
            UISettings uiSettings = new UISettings();
            var color = uiSettings.GetColorValue(UIColorType.Foreground);
            if (settingTheme != 0)
            {
                color = settingTheme == 1 ? Colors.Black : Colors.White;

            }
            ApplicationViewTitleBar titleBar = ApplicationView.GetForCurrentView().TitleBar;

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = color;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.BackgroundColor = Colors.Transparent;
        }
        private void HideTitleBar(bool hide)
        {
            if (SettingHelper.GetValue(SettingHelper.NEW_WINDOW_LIVEROOM, true))
            {
                if (hide)
                {
                    Grid.SetRow(GridContent, 0);
                    Grid.SetRowSpan(GridContent, 2);
                    TitleBarGrid.Visibility = Visibility.Collapsed;
                    Window.Current.SetTitleBar(null);
                }
                else
                {
                    Grid.SetRow(GridContent, 1);
                    Grid.SetRowSpan(GridContent, 1);
                    TitleBarGrid.Visibility = Visibility.Visible;
                    Window.Current.SetTitleBar(TitleBar);
                }
            }
            else
            {
                MessageCenter.HideTitlebar(hide);
            }
        }
        private void ClearXboxSettingBind()
        {
            XboxSuperChat.ClearValue(ListView.ItemsSourceProperty);
            xboxSettingsDMSize.ClearValue(ComboBox.SelectedValueProperty);
            xboxSettingsDecoder.ClearValue(ToggleSwitch.IsOnProperty);
            xboxSettingsDMArea.ClearValue(ComboBox.SelectedIndexProperty);
            xboxSettingsDMOpacity.ClearValue(ComboBox.SelectedValueProperty);
            xboxSettingsDMSpeed.ClearValue(ComboBox.SelectedValueProperty);
            xboxSettingsDMStyle.ClearValue(ComboBox.SelectedValueProperty);
            xboxSettingsDMColorful.ClearValue(ToggleSwitch.IsOnProperty);
            xboxSettingsDMBold.ClearValue(ToggleSwitch.IsOnProperty);
        }

        #region 播放器事件
        private async void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                var sessionSnapshot = BuildPlaybackSessionSnapshot(sender?.PlaybackSession);
                if (!string.IsNullOrEmpty(sessionSnapshot))
                {
                    LogHelper.Log($"媒体播放结束\n{sessionSnapshot}", LogType.DEBUG);
                }
                var earlyProbe = BuildEarlyEndProbe(sender?.PlaybackSession, "播放结束");
                if (!string.IsNullOrEmpty(earlyProbe))
                {
                    LogHelper.Log(earlyProbe, LogType.DEBUG);
                }
                if (TryRefreshHuyaPlayUrls("播放结束"))
                {
                    return;
                }
                if (TryFallbackBilibiliCodec("播放结束"))
                {
                    return;
                }
                if (TryRetryCurrentLine("播放结束"))
                {
                    return;
                }
                var index = liveRoomVM.Lines.IndexOf(liveRoomVM.CurrentLine);
                //尝试切换
                if (index == liveRoomVM.Lines.Count - 1)
                {
                    liveRoomVM.Living = false;
                }
                else
                {
                    liveRoomVM.CurrentLine = liveRoomVM.Lines[index + 1];
                }
            });
        }

        private async void PlaybackSession_BufferingEnded(MediaPlaybackSession sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                PlayerLoading.Visibility = Visibility.Collapsed;
                StopBufferingTimer();
                LogBufferingEnded(sender);
            });

        }

        private async void PlaybackSession_BufferingProgressChanged(MediaPlaybackSession sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                PlayerLoadText.Text = sender.BufferingProgress.ToString("p");
            });
        }

        private async void PlaybackSession_BufferingStarted(MediaPlaybackSession sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                PlayerLoading.Visibility = Visibility.Visible;
                PlayerLoadText.Text = "缓冲中";
                LogBufferingStarted(sender);
                StartBufferingTimer();
            });
        }

        private async void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await EnsureDiagnosticsSnapshotAsync();
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                var extra = new StringBuilder();
                extra.AppendLine($"MediaPlayerError: {args.Error}");
                if (args.ExtendedErrorCode != null)
                {
                    extra.AppendLine($"ExtendedErrorCode: 0x{args.ExtendedErrorCode.HResult:X8}");
                    if (!string.IsNullOrEmpty(args.ExtendedErrorCode.Message))
                    {
                        extra.AppendLine($"ExtendedMessage: {args.ExtendedErrorCode.Message}");
                    }
                }
                var sessionSnapshot = BuildPlaybackSessionSnapshot(sender?.PlaybackSession);
                if (!string.IsNullOrEmpty(sessionSnapshot))
                {
                    extra.AppendLine(sessionSnapshot);
                }
                var mergedExtra = JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, lastProbeSnapshot, extra.ToString().TrimEnd());
                LogPlayError("播放器播放失败", args.ExtendedErrorCode, mergedExtra);

                if (TryScheduleStreamReconnect($"MediaFailed/{args.Error}"))
                {
                    return;
                }
                PlayError();
            });

        }

        private async void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                //保持屏幕常亮
                dispRequest.RequestActive();
                PlayerLoading.Visibility = Visibility.Collapsed;
                lastMediaOpenedUtc = DateTimeOffset.UtcNow;
                SetMediaInfo();
                LogPlaybackMediaInfo("MediaOpened");
            });
        }

        private async void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (sender != null && lastPlaybackState != sender.PlaybackState)
                {
                    lastPlaybackState = sender.PlaybackState;
                    var sessionSnapshot = BuildPlaybackSessionSnapshot(sender);
                    if (!string.IsNullOrEmpty(sessionSnapshot))
                    {
                        LogHelper.Log($"播放状态变更: {sender.PlaybackState}\n{sessionSnapshot}", LogType.DEBUG);
                    }
                    if (sender.PlaybackState == MediaPlaybackState.Playing)
                    {
                        lastPlaybackStartUtc = DateTimeOffset.UtcNow;
                        lastPlaybackUrl = liveRoomVM?.CurrentLine?.Url;
                        LogPlaybackMediaInfo("Playing");
                        StartPlaybackSampling(System.Threading.Volatile.Read(ref mediaSourceAttemptVersion));
                    }
                }
                switch (sender.PlaybackState)
                {
                    case MediaPlaybackState.None:
                        break;
                    case MediaPlaybackState.Opening:
                        PlayerLoading.Visibility = Visibility.Visible;
                        PlayerLoadText.Text = "加载中";
                        break;
                    case MediaPlaybackState.Buffering:
                        PlayerLoading.Visibility = Visibility.Visible;
                        PlayerLoadText.Text = "缓冲中";
                        break;
                    case MediaPlaybackState.Playing:
                        PlayerLoading.Visibility = Visibility.Collapsed;
                        PlayBtnPlay.Visibility = Visibility.Collapsed;
                        PlayBtnPause.Visibility = Visibility.Visible;
                        dispRequest.RequestActive();
                        liveRoomVM.Living = true;
                        SetMediaInfo();
                        ResetStreamReconnectState();
                        StopBufferingTimer();
                        break;
                    case MediaPlaybackState.Paused:
                        PlayerLoading.Visibility = Visibility.Collapsed;
                        PlayBtnPlay.Visibility = Visibility.Visible;
                        PlayBtnPause.Visibility = Visibility.Collapsed;
                        break;
                    default:
                        break;
                }
            });
        }

        private void SetMediaInfo()
        {
            try
            {

                var str = $"Url: {liveRoomVM.CurrentLine?.Url ?? ""}\r\n";
                str += $"Quality: {liveRoomVM.CurrentQuality?.Quality ?? ""}\r\n";
                str += $"Video Codec: {interopMSS.CurrentVideoStream.CodecName}\r\nAudio Codec:{interopMSS.AudioStreams[0].CodecName}\r\n";
                str += $"Resolution: {interopMSS.CurrentVideoStream.PixelWidth} x {interopMSS.CurrentVideoStream.PixelHeight}\r\n";
                str += $"Video Bitrate: {interopMSS.CurrentVideoStream.Bitrate / 1024} Kbps\r\n";
                str += $"Audio Bitrate: {interopMSS.AudioStreams[0].Bitrate / 1024} Kbps\r\n";
                str += $"Decoder Engine: {interopMSS.CurrentVideoStream.DecoderEngine.ToString()}";
                txtInfo.Text = str;
            }
            catch (Exception ex)
            {
                txtInfo.Text = $"读取信息失败\r\n{ex.Message}";
            }



        }

        private void LogPlaybackMediaInfo(string source)
        {
            var mediaInfo = BuildPlaybackMediaInfoSnapshot(source);
            if (string.IsNullOrWhiteSpace(mediaInfo))
            {
                return;
            }

            lastMediaInfoSnapshot = mediaInfo;
            LogHelper.Log(mediaInfo, LogType.DEBUG);
        }

        private string BuildPlaybackMediaInfoSnapshot(string source)
        {
            if (interopMSS == null)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"播放媒体信息 source={source}");
            var context = BuildSafePlaybackContext();
            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine(context);
            }
            sb.AppendLine($"解码器索引: {GetEffectiveVideoDecoderIndex()}");
            sb.AppendLine($"解码器日志值: {GetDecoderLogText(GetEffectiveVideoDecoderIndex())}");

            try
            {
                var videoStream = interopMSS.CurrentVideoStream;
                if (videoStream != null)
                {
                    AppendPlaybackValue(sb, "Video Codec", () => videoStream.CodecName);
                    AppendPlaybackValue(sb, "Resolution", () => $"{videoStream.PixelWidth} x {videoStream.PixelHeight}");
                    AppendPlaybackValue(sb, "Video Bitrate", () => $"{videoStream.Bitrate / 1024} Kbps");
                    AppendPlaybackValue(sb, "Decoder Engine", () => videoStream.DecoderEngine.ToString());
                }
                else
                {
                    sb.AppendLine("Video Stream: null");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Video Stream读取失败: {BuildExceptionSummary(ex)}");
            }

            try
            {
                if (interopMSS.AudioStreams != null && interopMSS.AudioStreams.Count > 0)
                {
                    var audioStream = interopMSS.AudioStreams[0];
                    AppendPlaybackValue(sb, "Audio Codec", () => audioStream.CodecName);
                    AppendPlaybackValue(sb, "Audio Bitrate", () => $"{audioStream.Bitrate / 1024} Kbps");
                }
                else
                {
                    sb.AppendLine("Audio Stream: 空");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Audio Stream读取失败: {BuildExceptionSummary(ex)}");
            }

            var sessionSnapshot = BuildPlaybackSessionSnapshot(mediaPlayer?.PlaybackSession);
            if (!string.IsNullOrEmpty(sessionSnapshot))
            {
                sb.AppendLine(sessionSnapshot);
            }

            return sb.ToString().TrimEnd();
        }

        private void LogBufferingStarted(MediaPlaybackSession session)
        {
            currentBufferingStartedUtc = DateTimeOffset.UtcNow;
            var snapshot = BuildPlaybackSessionSnapshot(session);
            LogHelper.Log(JoinNonEmpty(
                "BufferingStarted",
                BuildSafePlaybackContext(),
                snapshot), LogType.DEBUG);
        }

        private void LogBufferingEnded(MediaPlaybackSession session)
        {
            TimeSpan? duration = null;
            if (currentBufferingStartedUtc.HasValue)
            {
                duration = DateTimeOffset.UtcNow - currentBufferingStartedUtc.Value;
            }
            currentBufferingStartedUtc = null;

            var durationText = duration.HasValue
                ? $"BufferingEnded durationMs={duration.Value.TotalMilliseconds:F0}"
                : "BufferingEnded durationMs=unknown";
            var snapshot = BuildPlaybackSessionSnapshot(session);
            LogHelper.Log(JoinNonEmpty(
                durationText,
                BuildSafePlaybackContext(),
                snapshot), LogType.DEBUG);
        }

        private void StartPlaybackSampling(int attemptVersion)
        {
            if (attemptVersion <= 0 || !IsMediaSourceAttemptCurrent(attemptVersion))
            {
                return;
            }

            CancelPlaybackSampling();
            playbackStallProbeStarted = false;
            playbackSampleHistory.Clear();
            var cts = new System.Threading.CancellationTokenSource();
            playbackSamplingCts = cts;
            _ = RunPlaybackSamplingAsync(attemptVersion, cts);
        }

        private async Task RunPlaybackSamplingAsync(int attemptVersion, System.Threading.CancellationTokenSource cts)
        {
            var token = cts.Token;
            var startedUtc = DateTimeOffset.UtcNow;
            DateTimeOffset? previousUtc = null;
            TimeSpan? previousPosition = null;
            var sampleIndex = 0;

            try
            {
                while (!token.IsCancellationRequested &&
                    IsMediaSourceAttemptCurrent(attemptVersion) &&
                    DateTimeOffset.UtcNow - startedUtc < PlaybackSampleDuration)
                {
                    await Task.Delay(PlaybackSampleInterval, token);
                    if (token.IsCancellationRequested || !IsMediaSourceAttemptCurrent(attemptVersion))
                    {
                        return;
                    }

                    PlaybackSampleResult sample = null;
                    var now = DateTimeOffset.UtcNow;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (token.IsCancellationRequested || !IsMediaSourceAttemptCurrent(attemptVersion))
                        {
                            return;
                        }
                        sampleIndex++;
                        sample = BuildPlaybackSample(sampleIndex, now, previousUtc, previousPosition);
                        if (sample != null && !string.IsNullOrEmpty(sample.Text))
                        {
                            AddPlaybackSampleHistory(sample.Text);
                            LogHelper.Log(sample.Text, LogType.DEBUG);
                        }
                        previousUtc = now;
                        previousPosition = TryReadPlaybackPosition(mediaPlayer?.PlaybackSession);
                    });

                    if (sample != null && sample.Suspicious && !playbackStallProbeStarted)
                    {
                        playbackStallProbeStarted = true;
                        StartPlaybackStallProbe(sample.Url, sample.Text);
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogHelper.Log($"播放采样异常: {BuildExceptionSummary(ex)}", LogType.DEBUG);
            }
            finally
            {
                if (playbackSamplingCts == cts)
                {
                    playbackSamplingCts = null;
                }
                cts.Dispose();
            }
        }

        private PlaybackSampleResult BuildPlaybackSample(int sampleIndex, DateTimeOffset now, DateTimeOffset? previousUtc, TimeSpan? previousPosition)
        {
            var session = mediaPlayer?.PlaybackSession;
            if (session == null)
            {
                return null;
            }

            var position = TryReadPlaybackPosition(session);
            var state = TryReadPlaybackState(session);
            var wallDelta = previousUtc.HasValue ? now - previousUtc.Value : (TimeSpan?)null;
            var positionDelta = previousPosition.HasValue && position.HasValue ? position.Value - previousPosition.Value : (TimeSpan?)null;
            var suspicious = state == MediaPlaybackState.Playing &&
                wallDelta.HasValue &&
                wallDelta.Value.TotalSeconds >= 1.5 &&
                (!positionDelta.HasValue || positionDelta.Value.TotalSeconds < 0.3);

            var sb = new StringBuilder();
            sb.AppendLine($"播放采样 #{sampleIndex}");
            sb.AppendLine($"Utc: {now:O}");
            sb.AppendLine($"State: {(state.HasValue ? state.Value.ToString() : "unknown")}");
            sb.AppendLine($"Position: {(position.HasValue ? position.Value.ToString() : "unknown")}");
            if (wallDelta.HasValue)
            {
                sb.AppendLine($"WallDeltaMs: {wallDelta.Value.TotalMilliseconds:F0}");
            }
            if (positionDelta.HasValue)
            {
                sb.AppendLine($"PositionDeltaMs: {positionDelta.Value.TotalMilliseconds:F0}");
            }
            else if (previousPosition.HasValue)
            {
                sb.AppendLine("PositionDeltaMs: unknown");
            }
            sb.AppendLine($"疑似进度停滞: {suspicious}");
            AppendPlaybackValue(sb, "BufferingProgress", () => session.BufferingProgress.ToString("P"));
            AppendPlaybackValue(sb, "DownloadProgress", () => session.DownloadProgress.ToString("P"));
            AppendPlaybackValue(sb, "PlaybackRate", () => session.PlaybackRate.ToString());
            sb.AppendLine($"AppMemory: {Windows.System.MemoryManager.AppMemoryUsage}");
            sb.AppendLine($"ManagedMemory: {GC.GetTotalMemory(false)}");
            var urlSummary = BuildUrlSummary(liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl);
            if (!string.IsNullOrEmpty(urlSummary))
            {
                sb.AppendLine(urlSummary);
            }

            return new PlaybackSampleResult
            {
                Text = sb.ToString().TrimEnd(),
                Suspicious = suspicious,
                Url = liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl
            };
        }

        private void StartPlaybackStallProbe(string url, string sampleText)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                var probe = await ProbeUrlAsync(url);
                var flvProbe = await ProbeFlvAsync(url);
                var merged = JoinNonEmpty(
                    "疑似播放进度停滞预检",
                    lastMediaInfoSnapshot,
                    sampleText,
                    BuildRecentPlaybackSamplesSnapshot(),
                    probe,
                    flvProbe);
                if (!string.IsNullOrEmpty(merged))
                {
                    LogHelper.Log(merged, LogType.DEBUG);
                }
            });
        }

        private void CancelPlaybackSampling()
        {
            var cts = playbackSamplingCts;
            playbackSamplingCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private void ResetPlaybackDiagnosticsForNewAttempt()
        {
            CancelPlaybackSampling();
            playbackStallProbeStarted = false;
            currentBufferingStartedUtc = null;
            lastMediaInfoSnapshot = null;
            playbackSampleHistory.Clear();
        }

        private void AddPlaybackSampleHistory(string sample)
        {
            if (string.IsNullOrWhiteSpace(sample))
            {
                return;
            }

            playbackSampleHistory.Enqueue(sample);
            while (playbackSampleHistory.Count > MaxPlaybackSampleHistory)
            {
                playbackSampleHistory.Dequeue();
            }
        }

        private string BuildRecentPlaybackSamplesSnapshot()
        {
            if (playbackSampleHistory.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine("最近播放采样:");
            foreach (var sample in playbackSampleHistory)
            {
                sb.AppendLine(sample);
            }
            return sb.ToString().TrimEnd();
        }

        private TimeSpan? TryReadPlaybackPosition(MediaPlaybackSession session)
        {
            try
            {
                return session?.Position;
            }
            catch
            {
                return null;
            }
        }

        private MediaPlaybackState? TryReadPlaybackState(MediaPlaybackSession session)
        {
            try
            {
                return session?.PlaybackState;
            }
            catch
            {
                return null;
            }
        }

        #endregion



        private void LiveRoomVM_AddDanmaku(object sender, LiveMessage e)
        {

            if (DanmuControl.Visibility == Visibility.Visible)
            {
                var color = DanmuSettingColourful.IsOn ?
                    Color.FromArgb(e.Color.A, e.Color.R, e.Color.G, e.Color.B) :
                    Colors.White;
                DanmuControl.AddLiveDanmu(e.Message, false, color);
            }

        }

        private async void CoreWindow_KeyDown(Windows.UI.Core.CoreWindow sender, Windows.UI.Core.KeyEventArgs args)
        {
            var elent = FocusManager.GetFocusedElement();
            if (elent is TextBox || elent is AutoSuggestBox)
            {
                args.Handled = false;
                return;
            }
            if (XBoxSplitView.IsPaneOpen)
            {
                if (args.VirtualKey == Windows.System.VirtualKey.GamepadMenu)
                {
                    XBoxSplitView.IsPaneOpen = false;
                    args.Handled = true;
                    return;
                }
                if (args.VirtualKey == Windows.System.VirtualKey.GamepadB)
                {
                    if (XboxSuperChat.Visibility == Visibility.Visible)
                    {
                        XBoxSplitView.IsPaneOpen = false;
                    }
                    args.Handled = true;
                    return;
                }
                args.Handled = false;
                return;
            }
            args.Handled = true;
            switch (args.VirtualKey)
            {
                //case Windows.System.VirtualKey.Space:
                //    if (mediaPlayer.PlaybackSession.CanPause)
                //    {
                //        mediaPlayer.Pause();
                //    }
                //    else
                //    {
                //        mediaPlayer.Play();
                //    }
                //    break;

                case Windows.System.VirtualKey.Up:
                    if (mediaPlayer.Volume + 0.1 > 1)
                    {
                        mediaPlayer.Volume = 1;
                    }
                    else
                    {
                        mediaPlayer.Volume += 0.1;
                    }


                    TxtToolTip.Text = "音量:" + mediaPlayer.Volume.ToString("P");
                    ToolTip.Visibility = Visibility.Visible;
                    await Task.Delay(2000);
                    ToolTip.Visibility = Visibility.Collapsed;
                    break;

                case Windows.System.VirtualKey.Down:
                    if (mediaPlayer.Volume - 0.1 < 0)
                    {
                        mediaPlayer.Volume = 0;
                    }
                    else
                    {
                        mediaPlayer.Volume -= 0.1;
                    }


                    if (mediaPlayer.Volume == 0)
                    {
                        TxtToolTip.Text = "静音";
                    }
                    else
                    {
                        TxtToolTip.Text = "音量:" + mediaPlayer.Volume.ToString("P");
                    }
                    ToolTip.Visibility = Visibility.Visible;
                    await Task.Delay(2000);
                    ToolTip.Visibility = Visibility.Collapsed;
                    break;
                case Windows.System.VirtualKey.Escape:
                    SetFullScreen(false);

                    break;
                case Windows.System.VirtualKey.F8:
                case Windows.System.VirtualKey.T:
                    //小窗播放
                    MiniWidnows(BottomBtnExitMiniWindows.Visibility == Visibility.Visible);

                    break;
                case Windows.System.VirtualKey.F12:
                case Windows.System.VirtualKey.W:
                    SetFullWindow(PlayBtnFullWindow.Visibility == Visibility.Visible);
                    break;
                case Windows.System.VirtualKey.F11:
                case Windows.System.VirtualKey.F:
                case Windows.System.VirtualKey.Enter:
                    SetFullScreen(PlayBtnFullScreen.Visibility == Visibility.Visible);
                    break;
                case Windows.System.VirtualKey.F10:
                    await CaptureVideo();
                    break;
                case Windows.System.VirtualKey.F9:
                case Windows.System.VirtualKey.D:
                case Windows.System.VirtualKey.GamepadX:
                    //if (DanmuControl.Visibility == Visibility.Visible)
                    //{
                    //    DanmuControl.Visibility = Visibility.Collapsed;

                    //}
                    //else
                    //{
                    //    DanmuControl.Visibility = Visibility.Visible;
                    //}
                    PlaySWDanmu.IsOn = DanmuControl.Visibility != Visibility.Visible;
                    break;
                case Windows.System.VirtualKey.GamepadA:
                    ShowControl(control.Visibility == Visibility.Collapsed);
                    break;
                case Windows.System.VirtualKey.GamepadMenu:
                    //打开设置
                    XBoxSettings.Visibility = Visibility.Visible;
                    XboxSuperChat.Visibility = Visibility.Collapsed;
                    XBoxSplitView.IsPaneOpen = true;
                    break;
                case Windows.System.VirtualKey.GamepadLeftTrigger:
                    //刷新直播间
                    BottomBtnRefresh_Click(this, null);
                    break;
                case Windows.System.VirtualKey.GamepadB:
                    //退出直播间
                    this.Frame.GoBack();
                    break;
                case Windows.System.VirtualKey.GamepadY:
                    //查看SC
                    XBoxSettings.Visibility = Visibility.Collapsed;
                    XboxSuperChat.Visibility = Visibility.Visible;
                    XBoxSplitView.IsPaneOpen = true;
                    break;
                case Windows.System.VirtualKey.GamepadRightTrigger:
                    //关注/取消关注
                    if (liveRoomVM.IsFavorite)
                    {
                        liveRoomVM.RemoveFavoriteCommand.Execute(null);
                        Utils.ShowMessageToast("已取消关注");
                    }
                    else
                    {
                        liveRoomVM.AddFavoriteCommand.Execute(null);
                        Utils.ShowMessageToast("已添加关注");
                    }

                    break;
                default:
                    break;
            }
        }


        private void LiveRoomVM_ChangedPlayUrl(object sender, string e)
        {
            ResetStreamReconnectState();
            QueueSetPlayer(e, "ChangedPlayUrl");
        }

        private async void LiveRoomVM_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName != "RoomID" && e?.PropertyName != "SiteName")
            {
                return;
            }

            await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                LoadDecoderSetting();
            });
        }

        private void LiveRoomVM_ReconnectStatusChanged(object sender, ReconnectStatus status)
        {
            if (status == null) return;
            LogHelper.Log($"[重连状态] {status.Source} inProgress={status.IsReconnecting} {status.Attempt}/{status.MaxAttempt} {status.Message}", LogType.DEBUG);
            // 流重连的 UI 文案由 ReconnectStreamAsync 自行更新；此处只处理弹幕提示
            // 弹幕重连不强显 PlayerLoading（已有 Messages 系统消息），以避免遮挡直播画面
        }

        private bool IsNetworkConnected()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return false;
                return profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return false;
            }
        }

        private async void NetworkInformation_NetworkStatusChanged(object sender)
        {
            var connected = IsNetworkConnected();
            await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!connected)
                {
                    if (!isNetworkDown)
                    {
                        isNetworkDown = true;
                        LogHelper.Log("网络已断开", LogType.DEBUG);
                        PlayerLoading.Visibility = Visibility.Visible;
                        PlayerLoadText.Text = "网络已断开，等待恢复...";
                        StopBufferingTimer();
                    }
                }
                else
                {
                    if (isNetworkDown)
                    {
                        isNetworkDown = false;
                        LogHelper.Log("网络已恢复，触发重连", LogType.DEBUG);
                        TryScheduleStreamReconnect("NetworkRestored");
                    }
                }
            });
        }

        private void StartBufferingTimer()
        {
            if (bufferingTimer == null) return;
            bufferingTimer.Stop();
            bufferingTimer.Start();
        }

        private void StopBufferingTimer()
        {
            bufferingTimer?.Stop();
        }

        private void BufferingTimer_Tick(object sender, object e)
        {
            bufferingTimer.Stop();
            var state = mediaPlayer?.PlaybackSession?.PlaybackState;
            if (state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening)
            {
                LogHelper.Log($"Buffering 超过 {BufferingTimeout.TotalSeconds:F0}s，触发流重连。State={state}", LogType.DEBUG);
                TryScheduleStreamReconnect("BufferingTimeout");
            }
        }

        private void ResetStreamReconnectState()
        {
            streamReconnectAttempt = 0;
            streamReconnectLevel = 0;
            CancelStreamReconnect();
            System.Threading.Interlocked.Exchange(ref streamReconnectInProgress, 0);
        }

        private void QueueSetPlayer(string url, string source)
        {
            if (isPageClosing)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            bool startWorker = false;
            var now = DateTimeOffset.UtcNow;
            lock (setPlayerRequestLock)
            {
                if (IsDuplicateSetPlayerRequest(url, now, source))
                {
                    LogHelper.Log($"忽略重复播放请求 source={source} url={url}", LogType.DEBUG);
                    return;
                }

                InvalidateMediaSourceAttempt();
                pendingSetPlayerUrl = url;
                pendingSetPlayerUtc = now;
                if (!setPlayerWorkerRunning)
                {
                    setPlayerWorkerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                _ = ProcessSetPlayerQueueAsync();
            }
        }

        private bool IsDuplicateSetPlayerRequest(string url, DateTimeOffset now, string source)
        {
            var allowRetryOfActiveUrl =
                string.Equals(source, "RetryCurrentLine", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(activeSetPlayerUrl) &&
                string.Equals(activeSetPlayerUrl, url, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(pendingSetPlayerUrl);

            if (!string.IsNullOrEmpty(activeSetPlayerUrl) &&
                string.Equals(activeSetPlayerUrl, url, StringComparison.OrdinalIgnoreCase) &&
                (now - activeSetPlayerUtc) <= DuplicateSetPlayerWindow &&
                !allowRetryOfActiveUrl)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(pendingSetPlayerUrl) &&
                string.Equals(pendingSetPlayerUrl, url, StringComparison.OrdinalIgnoreCase) &&
                (now - pendingSetPlayerUtc) <= DuplicateSetPlayerWindow)
            {
                return true;
            }

            return false;
        }

        private async Task ProcessSetPlayerQueueAsync()
        {
            while (true)
            {
                if (isPageClosing)
                {
                    ResetSetPlayerQueue();
                    return;
                }

                string nextUrl;
                bool shouldExit = false;
                lock (setPlayerRequestLock)
                {
                    nextUrl = pendingSetPlayerUrl;
                    pendingSetPlayerUrl = null;
                    if (string.IsNullOrWhiteSpace(nextUrl))
                    {
                        activeSetPlayerUrl = null;
                        setPlayerWorkerRunning = false;
                        return;
                    }

                    activeSetPlayerUrl = nextUrl;
                    activeSetPlayerUtc = DateTimeOffset.UtcNow;
                }

                try
                {
                    if (!isPageClosing)
                    {
                        await SetPlayer(nextUrl);
                    }
                }
                finally
                {
                    lock (setPlayerRequestLock)
                    {
                        if (string.Equals(activeSetPlayerUrl, nextUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            activeSetPlayerUrl = null;
                        }

                        if (string.IsNullOrWhiteSpace(pendingSetPlayerUrl))
                        {
                            setPlayerWorkerRunning = false;
                            shouldExit = true;
                        }
                    }
                }

                if (shouldExit)
                {
                    return;
                }
            }
        }

        private void CancelStreamReconnect()
        {
            var cts = streamReconnectCts;
            streamReconnectCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private void ResetSetPlayerQueue()
        {
            lock (setPlayerRequestLock)
            {
                pendingSetPlayerUrl = null;
                activeSetPlayerUrl = null;
                setPlayerWorkerRunning = false;
            }
        }

        private int BeginMediaSourceAttempt()
        {
            ResetPlaybackDiagnosticsForNewAttempt();
            return System.Threading.Interlocked.Increment(ref mediaSourceAttemptVersion);
        }

        private void InvalidateMediaSourceAttempt()
        {
            CancelPlaybackSampling();
            System.Threading.Interlocked.Increment(ref mediaSourceAttemptVersion);
        }

        private bool IsMediaSourceAttemptCurrent(int attemptVersion)
        {
            return !isPageClosing &&
                System.Threading.Volatile.Read(ref mediaSourceAttemptVersion) == attemptVersion;
        }

        private bool TryScheduleStreamReconnect(string reason)
        {
            if (liveRoomVM == null || liveRoomVM.Loading) return false;
            if (liveRoomVM.CurrentLine == null || string.IsNullOrEmpty(liveRoomVM.CurrentLine.Url)) return false;
            if (System.Threading.Interlocked.CompareExchange(ref streamReconnectInProgress, 1, 0) != 0)
            {
                return true;
            }
            _ = ReconnectStreamAsync(reason);
            return true;
        }

        private async Task ReconnectStreamAsync(string reason)
        {
            CancelStreamReconnect();
            var cts = new System.Threading.CancellationTokenSource();
            streamReconnectCts = cts;
            var token = cts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (streamReconnectAttempt >= MaxStreamReconnectAttempts)
                    {
                        PlayerLoadText.Text = "自动重连失败，请点击刷新重试";
                        LogHelper.Log("流重连已达上限", LogType.ERROR);
                        return;
                    }

                    var delay = StreamReconnectDelays[Math.Min(streamReconnectAttempt, StreamReconnectDelays.Length - 1)];
                    streamReconnectAttempt++;
                    var level = streamReconnectLevel;
                    var levelText = level == 0 ? "重连当前线路" : level == 1 ? "刷新播放地址" : "重新加载直播间";
                    PlayerLoading.Visibility = Visibility.Visible;
                    PlayerLoadText.Text = $"直播流重连中 ({streamReconnectAttempt}/{MaxStreamReconnectAttempts})：{levelText}";
                    LogHelper.Log($"流重连调度 reason={reason} attempt={streamReconnectAttempt} level={level} delay={delay.TotalSeconds}s", LogType.DEBUG);

                    try { await Task.Delay(delay, token); }
                    catch (TaskCanceledException) { return; }
                    if (token.IsCancellationRequested) return;
                    if (isNetworkDown) { continue; }

                    bool actionSucceeded = false;
                    try
                    {
                        if (level == 0)
                        {
                            var url = liveRoomVM?.CurrentLine?.Url;
                            if (!string.IsNullOrEmpty(url))
                            {
                                QueueSetPlayer(url, $"Reconnect/{reason}");
                            }
                        }
                        else if (level == 1)
                        {
                            liveRoomVM.LoadPlayUrl();
                        }
                        else
                        {
                            if (pageArgs != null && !string.IsNullOrEmpty(liveRoomVM?.RoomID))
                            {
                                await liveRoomVM.StopAsync();
                                liveRoomVM.LoadData(pageArgs.Site, liveRoomVM.RoomID);
                            }
                        }

                        // 每 2 次尝试升一级
                        if (streamReconnectAttempt % 2 == 0 && streamReconnectLevel < 2)
                        {
                            streamReconnectLevel++;
                        }
                        actionSucceeded = true;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Log($"流重连第{streamReconnectAttempt}次失败", LogType.ERROR, ex);
                    }

                    if (actionSucceeded)
                    {
                        // 等待 10s 让 action 生效：进入 Playing 会 Cancel token 并退出；否则继续 while 进入下一轮
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        if (token.IsCancellationRequested) return;
                        var curState = mediaPlayer?.PlaybackSession?.PlaybackState;
                        if (curState == MediaPlaybackState.Playing)
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                if (streamReconnectCts == cts)
                {
                    streamReconnectCts = null;
                }
                cts.Dispose();
                if (token.IsCancellationRequested || streamReconnectAttempt >= MaxStreamReconnectAttempts)
                {
                    System.Threading.Interlocked.Exchange(ref streamReconnectInProgress, 0);
                }
            }
        }

        private string GetDecoderModeText()
        {
            var decoder = GetEffectiveVideoDecoderIndex();
            switch (decoder)
            {
                case 1:
                    return "系统硬解";
                case 2:
                    return "FFmpeg软件解码";
                default:
                    return "自动";
            }
        }
        private string BuildPlaybackContext()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(liveRoomVM?.SiteName))
            {
                sb.AppendLine($"站点: {liveRoomVM.SiteName}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.RoomID))
            {
                sb.AppendLine($"房间ID: {liveRoomVM.RoomID}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.Title))
            {
                sb.AppendLine($"标题: {liveRoomVM.Title}");
            }
            if (liveRoomVM?.SiteName == "哔哩哔哩直播")
            {
                sb.AppendLine($"哔哩登录: {BiliAccount.Instance?.Logined}");
                sb.AppendLine($"哔哩用户ID: {BiliAccount.Instance?.UserId}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.CurrentQuality?.Quality))
            {
                sb.AppendLine($"清晰度: {liveRoomVM.CurrentQuality.Quality}");
            }
            if (liveRoomVM?.CurrentQuality?.Data != null)
            {
                sb.AppendLine($"清晰度代码: {liveRoomVM.CurrentQuality.Data}");
            }
            if (liveRoomVM?.Lines != null && liveRoomVM.CurrentLine != null)
            {
                var lineIndex = liveRoomVM.Lines.IndexOf(liveRoomVM.CurrentLine);
                if (lineIndex >= 0)
                {
                    sb.AppendLine($"线路: {liveRoomVM.CurrentLine.Name} ({lineIndex + 1}/{liveRoomVM.Lines.Count})");
                }
                else
                {
                    sb.AppendLine($"线路: {liveRoomVM.CurrentLine.Name}");
                }
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.CurrentLine?.Url))
            {
                sb.AppendLine($"Url: {liveRoomVM.CurrentLine.Url}");
            }
            sb.AppendLine($"解码模式: {GetDecoderModeText()}");
            var state = mediaPlayer?.PlaybackSession?.PlaybackState.ToString();
            if (!string.IsNullOrEmpty(state))
            {
                sb.AppendLine($"播放器状态: {state}");
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildSafePlaybackContext()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(liveRoomVM?.SiteName))
            {
                sb.AppendLine($"站点: {liveRoomVM.SiteName}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.RoomID))
            {
                sb.AppendLine($"房间ID: {liveRoomVM.RoomID}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.Title))
            {
                sb.AppendLine($"标题: {liveRoomVM.Title}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.CurrentQuality?.Quality))
            {
                sb.AppendLine($"清晰度: {liveRoomVM.CurrentQuality.Quality}");
            }
            if (liveRoomVM?.Lines != null && liveRoomVM.CurrentLine != null)
            {
                var lineIndex = liveRoomVM.Lines.IndexOf(liveRoomVM.CurrentLine);
                if (lineIndex >= 0)
                {
                    sb.AppendLine($"线路: {liveRoomVM.CurrentLine.Name} ({lineIndex + 1}/{liveRoomVM.Lines.Count})");
                }
                else
                {
                    sb.AppendLine($"线路: {liveRoomVM.CurrentLine.Name}");
                }
            }
            sb.AppendLine($"解码模式: {GetDecoderModeText()}");
            var state = mediaPlayer?.PlaybackSession?.PlaybackState.ToString();
            if (!string.IsNullOrEmpty(state))
            {
                sb.AppendLine($"播放器状态: {state}");
            }
            var urlSummary = BuildUrlSummary(liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl);
            if (!string.IsNullOrEmpty(urlSummary))
            {
                sb.AppendLine(urlSummary);
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildConfigSnapshot(MediaSourceConfig config)
        {
            if (config == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.AppendLine("FFmpeg配置:");
            sb.AppendLine($"VideoDecoderMode: {config.Video?.VideoDecoderMode}");
            if (config.FFmpegOptions != null && config.FFmpegOptions.Count > 0)
            {
                foreach (var kv in config.FFmpegOptions)
                {
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                }
            }
            return sb.ToString().TrimEnd();
        }
        private string BuildUrlAnalysis(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("URL分析:");
                sb.AppendLine($"完整长度: {url.Length}");
                sb.AppendLine($"Scheme: {uri.Scheme}");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    var decoder = new WwwFormUrlDecoder(uri.Query);
                    foreach (var item in decoder)
                    {
                        if (string.Equals(item.Name, "expires", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Name, "deadline", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(item.Name, "wts", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryParseUnixSeconds(item.Value, out var dt))
                            {
                                sb.AppendLine($"{item.Name}={item.Value} ({dt:O}, 剩余 {(dt - DateTimeOffset.UtcNow).TotalSeconds:F0}s)");
                            }
                            else
                            {
                                sb.AppendLine($"{item.Name}={item.Value}");
                            }
                        }
                        else if (string.Equals(item.Name, "wsTime", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryParseHexUnixSeconds(item.Value, out var dt))
                            {
                                sb.AppendLine($"{item.Name}={item.Value} (hex-> {dt:O}, 剩余 {(dt - DateTimeOffset.UtcNow).TotalSeconds:F0}s)");
                            }
                            else
                            {
                                sb.AppendLine($"{item.Name}={item.Value}");
                            }
                        }
                        else if (string.Equals(item.Name, "qn", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "codec", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "format", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "uipk", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "uipv", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "platform", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"{item.Name}={item.Value}");
                        }
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"URL分析失败: {BuildExceptionSummary(ex)}";
            }
        }

        private string BuildUrlSummary(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("URL摘要:");
                sb.AppendLine($"Scheme: {uri.Scheme}");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                sb.AppendLine($"QueryLength: {uri.Query?.Length ?? 0}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"URL摘要失败: {BuildExceptionSummary(ex)}";
            }
        }

        private string BuildPlaybackSessionSnapshot(MediaPlaybackSession session)
        {
            if (session == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.AppendLine("PlaybackSession:");
            AppendPlaybackValue(sb, "State", () => session.PlaybackState.ToString());
            AppendPlaybackValue(sb, "Position", () => session.Position.ToString());
            AppendPlaybackValue(sb, "NaturalDuration", () => session.NaturalDuration.ToString());
            AppendPlaybackValue(sb, "BufferingProgress", () => session.BufferingProgress.ToString("P"));
            AppendPlaybackValue(sb, "DownloadProgress", () => session.DownloadProgress.ToString("P"));
            AppendPlaybackValue(sb, "CanSeek", () => session.CanSeek.ToString());
            AppendPlaybackValue(sb, "PlaybackRate", () => session.PlaybackRate.ToString());
            return sb.ToString().TrimEnd();
        }
        private string BuildEarlyEndProbe(MediaPlaybackSession session, string reason)
        {
            var url = liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl;
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            TimeSpan? duration = null;
            if (lastPlaybackStartUtc.HasValue)
            {
                duration = DateTimeOffset.UtcNow - lastPlaybackStartUtc.Value;
            }
            if (duration.HasValue && duration.Value > EarlyEndThreshold)
            {
                return null;
            }
            var reasonText = duration.HasValue ? $"{reason} duration={duration.Value.TotalSeconds:F2}s" : reason;
            _ = Task.Run(async () =>
            {
                var probe = await ProbeUrlAsync(url);
                var flvProbe = await ProbeFlvAsync(url);
                var merged = JoinNonEmpty(
                    $"短播放预检: {reasonText}",
                    lastMediaInfoSnapshot,
                    BuildRecentPlaybackSamplesSnapshot(),
                    probe,
                    flvProbe);
                if (!string.IsNullOrEmpty(merged))
                {
                    LogHelper.Log(merged, LogType.DEBUG);
                }
            });
            return $"短播放预检触发: {reasonText}";
        }
        private async Task<string> ProbeFlvAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("FLV样本预检:");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                const uint maxBytes = 262144;
                using (var client = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    ApplyProbeHeaders(request);
                    request.Headers.Append("Range", $"bytes=0-{maxBytes - 1}");
                    var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    sb.AppendLine($"GET Range=0-{maxBytes - 1} => {(int)response.StatusCode} {response.ReasonPhrase}");
                    if (response.Content?.Headers?.ContentType != null)
                    {
                        sb.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
                    }
                    if (response.Content?.Headers?.ContentLength != null)
                    {
                        sb.AppendLine($"Content-Length: {response.Content.Headers.ContentLength}");
                    }
                    using (var stream = await response.Content.ReadAsInputStreamAsync())
                    using (var reader = new DataReader(stream))
                    {
                        var loaded = await reader.LoadAsync(maxBytes);
                        if (loaded == 0)
                        {
                            sb.AppendLine("样本读取为空");
                        }
                        else
                        {
                            var buffer = new byte[loaded];
                            reader.ReadBytes(buffer);
                            sb.AppendLine(AnalyzeFlvTags(buffer, (int)loaded));
                        }
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                return $"FLV样本预检失败: {BuildExceptionSummary(ex)}";
            }
        }
        private string AnalyzeFlvTags(byte[] buffer, int length)
        {
            if (buffer == null || length <= 0)
            {
                return "FLV样本为空";
            }
            if (length < 13 || buffer[0] != (byte)'F' || buffer[1] != (byte)'L' || buffer[2] != (byte)'V')
            {
                return $"FLV样本签名异常 len={length}";
            }
            var offset = 9 + 4;
            var audio = 0;
            var video = 0;
            var script = 0;
            var tags = 0;
            var firstAudio = -1;
            var firstVideo = -1;
            while (offset + 11 <= length)
            {
                var tagType = buffer[offset];
                var dataSize = (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
                if (dataSize < 0)
                {
                    break;
                }
                if (tagType == 8)
                {
                    audio++;
                    if (firstAudio < 0) firstAudio = offset;
                }
                else if (tagType == 9)
                {
                    video++;
                    if (firstVideo < 0) firstVideo = offset;
                }
                else if (tagType == 18)
                {
                    script++;
                }
                tags++;
                var next = offset + 11 + dataSize + 4;
                if (next <= offset || next > length)
                {
                    break;
                }
                offset = next;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"FLV标签统计: tags={tags} audio={audio} video={video} script={script}");
            sb.AppendLine($"首音频偏移: {(firstAudio < 0 ? "无" : firstAudio.ToString())}");
            sb.AppendLine($"首视频偏移: {(firstVideo < 0 ? "无" : firstVideo.ToString())}");
            sb.AppendLine($"样本长度: {length}");
            return sb.ToString().TrimEnd();
        }
        private void AppendPlaybackValue(StringBuilder sb, string name, Func<string> getter)
        {
            try
            {
                sb.AppendLine($"{name}: {getter()}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name}: <err {ex.GetType().Name} 0x{ex.HResult:X8}>");
            }
        }
        private static bool TryParseUnixSeconds(string value, out DateTimeOffset dt)
        {
            dt = default;
            if (long.TryParse(value, out var seconds))
            {
                try
                {
                    if (seconds > 0)
                    {
                        dt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }
        private static bool TryParseHexUnixSeconds(string value, out DateTimeOffset dt)
        {
            dt = default;
            if (long.TryParse(value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                try
                {
                    if (seconds > 0)
                    {
                        dt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }
        private string BuildExceptionDetail(Exception ex)
        {
            if (ex == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"异常类型: {ex.GetType().FullName}");
            sb.AppendLine($"HResult: 0x{ex.HResult:X8}");
            if (!string.IsNullOrEmpty(ex.Message))
            {
                sb.AppendLine($"异常信息: {ex.Message}");
            }
            if (ex.TargetSite != null)
            {
                sb.AppendLine($"TargetSite: {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");
            }
            if (ex is AggregateException aggregate && aggregate.InnerExceptions != null && aggregate.InnerExceptions.Count > 0)
            {
                var index = 0;
                foreach (var inner in aggregate.InnerExceptions)
                {
                    sb.AppendLine($"AggregateInner[{index}]: {inner.GetType().FullName} (0x{inner.HResult:X8}) {inner.Message}");
                    index++;
                }
                return sb.ToString().TrimEnd();
            }
            var innerDepth = 0;
            var innerEx = ex.InnerException;
            while (innerEx != null && innerDepth < 4)
            {
                sb.AppendLine($"InnerException[{innerDepth}]: {innerEx.GetType().FullName}");
                sb.AppendLine($"InnerHResult: 0x{innerEx.HResult:X8}");
                if (!string.IsNullOrEmpty(innerEx.Message))
                {
                    sb.AppendLine($"InnerMessage: {innerEx.Message}");
                }
                innerEx = innerEx.InnerException;
                innerDepth++;
            }
            return sb.ToString().TrimEnd();
        }
        private string BuildExceptionSummary(Exception ex)
        {
            if (ex == null)
            {
                return null;
            }
            return $"{ex.GetType().FullName} (0x{ex.HResult:X8}): {ex.Message}";
        }
        private void ApplyProbeHeaders(HttpRequestMessage request)
        {
            if (liveRoomVM?.SiteName == "哔哩哔哩直播")
            {
                request.Headers.Append("User-Agent", BilibiliPlaybackUserAgent);
                request.Headers.Append("Referer", "https://live.bilibili.com/");
            }
            else if (liveRoomVM?.SiteName == "虎牙直播")
            {
                request.Headers.Append("User-Agent", "HYSDK(Windows, 30000002)_APP(pc_exe&6080100&official)_SDK(trans&2.23.0.4969)");
                request.Headers.Append("Referer", "https://m.huya.com/");
            }
        }
        private async Task<string> ProbeUrlAsync(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("URL预检:");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Scheme: {uri.Scheme}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                var headResult = await SendProbeAsync(uri, "HEAD", useRange: false);
                sb.AppendLine(headResult);
                var rangeResult = await SendProbeAsync(uri, "GET", useRange: true);
                sb.AppendLine(rangeResult);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"URL预检失败: {BuildExceptionSummary(ex)}";
            }
        }
        private string AnalyzeFirstBytes(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
            {
                return "首包字节为空";
            }
            var hex = BitConverter.ToString(buffer);
            var ascii = Encoding.ASCII.GetString(buffer, 0, Math.Min(buffer.Length, 32));
            var sb = new StringBuilder();
            sb.AppendLine($"首包HEX: {hex}");
            sb.AppendLine($"首包ASCII: {ascii}");
            if (buffer.Length >= 3 && buffer[0] == (byte)'F' && buffer[1] == (byte)'L' && buffer[2] == (byte)'V')
            {
                sb.AppendLine("首包签名: FLV");
            }
            else if (ascii.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("首包签名: M3U8");
            }
            else if (ascii.StartsWith("RIFF", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("首包签名: RIFF");
            }
            else
            {
                sb.AppendLine("首包签名: 未知");
            }
            return sb.ToString().TrimEnd();
        }
        private async Task<string> SendProbeAsync(Uri uri, string method, bool useRange)
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(new HttpMethod(method), uri))
            {
                ApplyProbeHeaders(request);
                if (useRange)
                {
                    request.Headers.Append("Range", "bytes=0-4095");
                }
                var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead);
                var sb = new StringBuilder();
                sb.AppendLine($"{method} {(useRange ? "Range=0-4095" : "")} => {(int)response.StatusCode} {response.ReasonPhrase}");
                if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != uri)
                {
                    sb.AppendLine($"Final-Uri: {response.RequestMessage.RequestUri}");
                }
                if (response.Headers.TryGetValue("Accept-Ranges", out string acceptRanges))
                {
                    sb.AppendLine($"Accept-Ranges: {acceptRanges}");
                }
                if (response.Content?.Headers?.ContentType != null)
                {
                    sb.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
                }
                if (response.Content?.Headers?.ContentLength != null)
                {
                    sb.AppendLine($"Content-Length: {response.Content.Headers.ContentLength}");
                }
                if (response.Content?.Headers?.ContentRange != null)
                {
                    sb.AppendLine($"Content-Range: {response.Content.Headers.ContentRange}");
                }
                if (useRange && response.Content != null)
                {
                    try
                    {
                        using (var stream = await response.Content.ReadAsInputStreamAsync())
                        using (var reader = new DataReader(stream))
                        {
                            var loaded = await reader.LoadAsync(32);
                            if (loaded > 0)
                            {
                                var buffer = new byte[loaded];
                                reader.ReadBytes(buffer);
                                sb.AppendLine(AnalyzeFirstBytes(buffer));
                            }
                            else
                            {
                                sb.AppendLine("首包读取为空");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"首包读取失败: {BuildExceptionSummary(ex)}");
                    }
                }
                return sb.ToString().TrimEnd();
            }
        }
        private static string JoinNonEmpty(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.AppendLine(part.TrimEnd());
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }
        private async Task EnsureDiagnosticsSnapshotAsync()
        {
            if (!string.IsNullOrEmpty(diagnosticsSnapshot))
            {
                return;
            }
            if (diagnosticsSnapshotTask == null)
            {
                diagnosticsSnapshotTask = BuildDiagnosticsSnapshotAsync();
            }
            await diagnosticsSnapshotTask;
        }
        private async Task BuildDiagnosticsSnapshotAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("环境信息:");
            try
            {
                sb.AppendLine($"设备族: {AnalyticsInfo.VersionInfo.DeviceFamily}");
                var deviceVersion = ulong.Parse(AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
                var major = (deviceVersion & 0xFFFF000000000000L) >> 48;
                var minor = (deviceVersion & 0x0000FFFF00000000L) >> 32;
                var build = (deviceVersion & 0x00000000FFFF0000L) >> 16;
                var revision = deviceVersion & 0x000000000000FFFFL;
                sb.AppendLine($"系统版本: {major}.{minor}.{build}.{revision}");
            }
            catch
            {
            }
            try
            {
                var package = Package.Current;
                var v = package.Id.Version;
                sb.AppendLine($"应用版本: {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                sb.AppendLine($"架构: {package.Id.Architecture}");
            }
            catch
            {
            }
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                var level = profile?.GetNetworkConnectivityLevel();
                sb.AppendLine($"网络: {(profile == null ? "无" : profile.ProfileName)}");
                sb.AppendLine($"连通性: {level}");
            }
            catch
            {
            }
            try
            {
                var folder = Package.Current.InstalledLocation;
                var libs = new[]
                {
                    "FFmpegInteropX.dll",
                    "avformat-59.dll",
                    "avcodec-59.dll",
                    "avutil-57.dll",
                    "swresample-4.dll",
                    "swscale-6.dll"
                };
                foreach (var lib in libs)
                {
                    var item = await folder.TryGetItemAsync(lib);
                    if (item != null)
                    {
                        var file = item as StorageFile;
                        var props = await file.GetBasicPropertiesAsync();
                        sb.AppendLine($"{lib}: {props.Size} bytes");
                    }
                    else
                    {
                        sb.AppendLine($"{lib}: 未找到");
                    }
                }
            }
            catch
            {
            }
            diagnosticsSnapshot = sb.ToString().TrimEnd();
        }
        private void LogPlayError(string title, Exception ex = null, string extra = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            var context = BuildPlaybackContext();
            if (!string.IsNullOrEmpty(context))
            {
                sb.AppendLine(context);
            }
            if (!string.IsNullOrEmpty(diagnosticsSnapshot))
            {
                sb.AppendLine(diagnosticsSnapshot);
            }
            if (!string.IsNullOrEmpty(lastMediaInfoSnapshot))
            {
                sb.AppendLine(lastMediaInfoSnapshot);
            }
            var samples = BuildRecentPlaybackSamplesSnapshot();
            if (!string.IsNullOrEmpty(samples))
            {
                sb.AppendLine(samples);
            }
            var exceptionDetail = BuildExceptionDetail(ex);
            if (!string.IsNullOrEmpty(exceptionDetail))
            {
                sb.AppendLine(exceptionDetail);
            }
            if (!string.IsNullOrEmpty(extra))
            {
                sb.AppendLine(extra);
            }
            var message = sb.ToString().TrimEnd();
            LogHelper.Log(message, LogType.ERROR, ex);
        }
        private async Task SetPlayer(string url)
        {
            var attemptVersion = 0;
            try
            {
                if (isPageClosing)
                {
                    return;
                }
                attemptVersion = BeginMediaSourceAttempt();
                PlayerLoading.Visibility = Visibility.Visible;
                PlayerLoadText.Text = "加载中";
                if (mediaPlayer != null)
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Source = null;
                }
                if (interopMSS != null)
                {
                    interopMSS.Dispose();
                    interopMSS = null;
                }

                var config = new MediaSourceConfig();
                config.FFmpegOptions.Add("rtsp_transport", "tcp");
                config.FFmpegOptions.Add("multiple_requests", "1");
                config.FFmpegOptions.Add("reconnect", "1");
                config.FFmpegOptions.Add("reconnect_at_eof", "1");
                config.FFmpegOptions.Add("reconnect_streamed", "1");
                config.FFmpegOptions.Add("reconnect_delay_max", "2");
                var decoder = GetEffectiveVideoDecoderIndex();
                switch (decoder)
                {
                    case 1:
                        config.Video.VideoDecoderMode = VideoDecoderMode.ForceSystemDecoder;
                        break;
                    case 2:
                        config.Video.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
                        break;
                    default:
                        config.Video.VideoDecoderMode = VideoDecoderMode.Automatic;
                        break;
                }
                if (liveRoomVM.SiteName == "哔哩哔哩直播")
                {
                    config.FFmpegOptions.Add("user_agent", BilibiliPlaybackUserAgent);
                    config.FFmpegOptions.Add("referer", "https://live.bilibili.com/");
                }
                else if (liveRoomVM.SiteName == "虎牙直播")
                {
                    //config.FFmpegOptions.Add("user_agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 16_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.6 Mobile/15E148 Safari/604.1");
                    //config.FFmpegOptions.Add("referer", "https://m.huya.com");

                    // from stream-rec url:https://github.com/stream-rec/stream-rec
                    //var sysTs = Utils.GetTimeStamp() / 1000;
                    //var validTs = 20000308;
                    //var last8 = sysTs % 100000000;
                    //var currentTs = last8 > validTs ? last8 : (validTs + sysTs / 100);
                    //config.FFmpegOptions.Add("user_agent", $"HYSDK(Windows, {currentTs})");
                    config.FFmpegOptions.Add("user_agent", "HYSDK(Windows, 30000002)_APP(pc_exe&6080100&official)_SDK(trans&2.23.0.4969)");
                    config.FFmpegOptions.Add("referer", "https://m.huya.com/");
                }
                lastPlaybackState = null;
                lastMediaOpenedUtc = null;
                lastPlaybackStartUtc = null;
                lastPlaybackUrl = url;
                lastConfigSnapshot = BuildConfigSnapshot(config);
                lastUrlAnalysis = BuildUrlAnalysis(url);
                lastProbeSnapshot = null;
                await EnsureDiagnosticsSnapshotAsync();
                if (!IsMediaSourceAttemptCurrent(attemptVersion))
                {
                    return;
                }
                var attemptLog = JoinNonEmpty("播放尝试", BuildPlaybackContext(), lastConfigSnapshot, lastUrlAnalysis);
                if (!string.IsNullOrEmpty(attemptLog))
                {
                    LogHelper.Log(attemptLog, LogType.DEBUG);
                }
                try
                {
                    var createTimeout = GetMediaSourceCreateTimeout(url);
                    var createStartedUtc = DateTimeOffset.UtcNow;
                    var createTask = FFmpegMediaSource.CreateFromUriAsync(url, config).AsTask();
                    if (createTimeout.HasValue)
                    {
                        var completedTask = await Task.WhenAny(createTask, Task.Delay(createTimeout.Value));
                        if (!IsMediaSourceAttemptCurrent(attemptVersion))
                        {
                            _ = DisposeLateMediaSourceAsync(createTask, url, attemptVersion);
                            return;
                        }
                        if (completedTask != createTask)
                        {
                            _ = DisposeLateMediaSourceAsync(createTask, url, attemptVersion);
                            var timeoutExtra = JoinNonEmpty(
                                lastConfigSnapshot,
                                lastUrlAnalysis,
                                $"播放器建源超时: {createTimeout.Value.TotalSeconds:F0}s elapsedMs={(DateTimeOffset.UtcNow - createStartedUtc).TotalMilliseconds:F0}");
                            LogPlayError("播放器初始化超时",
                                new TimeoutException($"FFmpegMediaSource.CreateFromUriAsync 超时 {createTimeout.Value.TotalSeconds:F0}s"),
                                timeoutExtra);
                            PlayError();
                            return;
                        }
                    }

                    var mediaSource = await createTask;
                    if (!IsMediaSourceAttemptCurrent(attemptVersion))
                    {
                        try { mediaSource?.Dispose(); } catch { }
                        return;
                    }
                    interopMSS = mediaSource;
                    if (createTimeout.HasValue)
                    {
                        LogHelper.Log($"播放器建源完成 elapsedMs={(DateTimeOffset.UtcNow - createStartedUtc).TotalMilliseconds:F0} 线路: {liveRoomVM?.CurrentLine?.Name}", LogType.DEBUG);
                    }
                }
                catch (Exception ex)
                {
                    if (!IsMediaSourceAttemptCurrent(attemptVersion))
                    {
                        return;
                    }
                    var probe = await ProbeUrlAsync(url);
                    lastProbeSnapshot = probe;
                    var mergedExtra = JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, probe);
                    LogPlayError("播放器初始化失败", ex, mergedExtra);
                    PlayError();
                    return;
                }

                mediaPlayer.AutoPlay = true;
                mediaPlayer.Volume = SliderVolume.Value;
                mediaPlayer.Source = interopMSS.CreateMediaPlaybackItem();
                player.SetMediaPlayer(mediaPlayer);
            }
            catch (Exception ex)
            {
                if (isPageClosing || (attemptVersion != 0 && !IsMediaSourceAttemptCurrent(attemptVersion)))
                {
                    return;
                }
                var probe = await ProbeUrlAsync(url);
                lastProbeSnapshot = probe;
                var mergedExtra = JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, probe);
                LogPlayError("播放失败", ex, mergedExtra);
                Utils.ShowMessageToast("播放失败" + ex.Message);
            }

        }

        private async Task DisposeLateMediaSourceAsync(Task<FFmpegMediaSource> createTask, string url, int attemptVersion)
        {
            try
            {
                var mediaSource = await createTask;
                mediaSource?.Dispose();
                if (isPageClosing)
                {
                    LogHelper.Log($"关闭后延迟建源已释放 urlHash={url?.GetHashCode()}", LogType.DEBUG);
                }
                else if (!IsMediaSourceAttemptCurrent(attemptVersion))
                {
                    LogHelper.Log($"过期建源已释放 urlHash={url?.GetHashCode()}", LogType.DEBUG);
                }
            }
            catch
            {
            }
        }

        private async void PlayError()
        {
            if (liveRoomVM.CurrentLine == null)
            {
                return;
            }
            if (TryRefreshHuyaPlayUrls("播放失败"))
            {
                return;
            }
            if (TryFallbackBilibiliCodec("播放失败"))
            {
                return;
            }
            if (TryRetryCurrentLine("播放失败"))
            {
                return;
            }
            // 当前线路播放失败，尝试下一个线路
            var index = liveRoomVM.Lines.IndexOf(liveRoomVM.CurrentLine);
            if (index == liveRoomVM.Lines.Count - 1)
            {
                PlayerLoading.Visibility = Visibility.Collapsed;
                LogPlayError("直播加载失败", null, JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, lastProbeSnapshot));
                await new MessageDialog($"啊，播放失败了，请尝试以下操作\r\n1、更换清晰度或线路\r\n2、请尝试在直播设置中打开/关闭硬解试试", "播放失败").ShowAsync();
            }
            else
            {
                liveRoomVM.CurrentLine = liveRoomVM.Lines[index + 1];
            }
        }

        private bool TryRefreshHuyaPlayUrls(string reason)
        {
            if (liveRoomVM == null || liveRoomVM.SiteName != "虎牙直播")
            {
                return false;
            }
            if (liveRoomVM.CurrentQuality == null)
            {
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            if (lastHuyaRefreshUtc.HasValue && (now - lastHuyaRefreshUtc.Value) < HuyaRefreshCooldown)
            {
                return false;
            }
            lastHuyaRefreshUtc = now;
            LogHelper.Log($"虎牙播放异常，尝试刷新播放地址。原因: {reason}", LogType.DEBUG);
            liveRoomVM.LoadPlayUrl();
            return true;
        }

        private bool TryFallbackBilibiliCodec(string reason)
        {
            if (liveRoomVM == null || liveRoomVM.SiteName != "哔哩哔哩直播")
            {
                return false;
            }

            if (string.Equals(liveRoomVM.SelectedCodec, "AVC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var currentCodec = liveRoomVM.CurrentLine?.Codec;
            if (string.IsNullOrWhiteSpace(currentCodec) || string.Equals(currentCodec, "AVC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (liveRoomVM.AvailableCodecs?.Any(x => string.Equals(x, "AVC", StringComparison.OrdinalIgnoreCase)) != true)
            {
                return false;
            }

            LogHelper.Log($"哔哩哔哩直播播放异常，自动从 {currentCodec} 切换到 AVC。原因: {reason}", LogType.DEBUG);
            liveRoomVM.SelectedCodec = "AVC";
            return true;
        }

        private TimeSpan? GetMediaSourceCreateTimeout(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (liveRoomVM?.SiteName == "哔哩哔哩直播" &&
                url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BilibiliCreateSourceTimeout;
            }

            return null;
        }

        private bool TryRetryCurrentLine(string reason)
        {
            var url = liveRoomVM?.CurrentLine?.Url;
            if (!ShouldRetryCurrentLine(url))
            {
                return false;
            }

            if (!string.Equals(currentLineRetryUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                currentLineRetryUrl = url;
                currentLineRetryCount = 0;
            }

            if (currentLineRetryCount >= 2)
            {
                return false;
            }

            currentLineRetryCount++;
            var delay = currentLineRetryCount > 1 ? CurrentLineRetryDelay : TimeSpan.Zero;
            LogHelper.Log($"哔哩哔哩直播播放异常，重试当前线路。原因: {reason} 次数: {currentLineRetryCount}/2 线路: {liveRoomVM?.CurrentLine?.Name}", LogType.DEBUG);
            _ = RetryCurrentLineAsync(url, delay);
            return true;
        }

        private bool ShouldRetryCurrentLine(string url)
        {
            if (liveRoomVM?.SiteName != "哔哩哔哩直播" || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (!lastPlaybackStartUtc.HasValue)
            {
                return true;
            }

            return (DateTimeOffset.UtcNow - lastPlaybackStartUtc.Value) <= BilibiliRetryWindow;
        }

        private async Task RetryCurrentLineAsync(string url, TimeSpan delay)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            await this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!string.Equals(liveRoomVM?.CurrentLine?.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                QueueSetPlayer(url, "RetryCurrentLine");
            });
        }

        private async Task StopPlayAsync()
        {
            ResetStreamReconnectState();
            ReleasePlaybackResources();

            timer_focus?.Stop();
            controlTimer?.Stop();
            Window.Current.CoreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Arrow, 0);

            if (liveRoomVM != null)
            {
                await liveRoomVM.StopAsync();
            }

            SetFullScreen(false);
            MiniWidnows(false);
            ReleaseDisplayRequest();
        }

        private void ReleasePlaybackResources()
        {
            InvalidateMediaSourceAttempt();
            StopBufferingTimer();
            ResetSetPlayerQueue();
            currentLineRetryUrl = null;
            currentLineRetryCount = 0;
            lastPlaybackState = null;
            lastMediaOpenedUtc = null;
            lastPlaybackStartUtc = null;
            lastPlaybackUrl = null;
            lastConfigSnapshot = null;
            lastUrlAnalysis = null;
            lastProbeSnapshot = null;
            lastMediaInfoSnapshot = null;
            diagnosticsSnapshot = null;
            diagnosticsSnapshotTask = null;
            playbackStallProbeStarted = false;
            currentBufferingStartedUtc = null;
            playbackSampleHistory.Clear();

            if (mediaPlayer != null)
            {
                try { mediaPlayer.Pause(); } catch { }
                try { mediaPlayer.Source = null; } catch { }
            }
            try { player.SetMediaPlayer(null); } catch { }

            if (interopMSS != null)
            {
                try { interopMSS.Dispose(); } catch { }
                interopMSS = null;
            }

            try { DanmuControl.ClearAll(); } catch { }
            PlayerLoading.Visibility = Visibility.Collapsed;
        }

        private void ReleaseDisplayRequest()
        {
            if (dispRequest != null)
            {
                try
                {
                    dispRequest.RequestRelease();
                }
                catch (Exception)
                {
                }

                dispRequest = null;
            }
        }
        private void ControlTimer_Tick(object sender, object e)
        {
            if (showControlsFlag != -1)
            {
                if (showControlsFlag >= 5)
                {
                    var elent = FocusManager.GetFocusedElement();
                    if (!(elent is TextBox) && !(elent is AutoSuggestBox))
                    {
                        ShowControl(false);
                        showControlsFlag = -1;
                    }
                }
                else
                {
                    showControlsFlag++;
                }
            }
        }

        private void Timer_focus_Tick(object sender, object e)
        {
            var elent = FocusManager.GetFocusedElement();
            if (elent is Button || elent is AppBarButton || elent is HyperlinkButton || elent is MenuFlyoutItem)
            {
                BtnFoucs.Focus(FocusState.Programmatic);
            }

        }

        private void LogLiveRoomMemory(string stage)
        {
            LogHelper.Log($"{stage}。AppMemory={Windows.System.MemoryManager.AppMemoryUsage} Managed={GC.GetTotalMemory(false)}", LogType.DEBUG);
        }

        private async Task CleanupLiveRoomPageAsync(bool clearWindowContent = false)
        {
            if (liveRoomCleaned)
            {
                return;
            }
            liveRoomCleaned = true;
            isPageClosing = true;
            LogLiveRoomMemory("直播间页面开始清理");

            liveRoomVM.ChangedPlayUrl -= LiveRoomVM_ChangedPlayUrl;
            liveRoomVM.AddDanmaku -= LiveRoomVM_AddDanmaku;
            liveRoomVM.ReconnectStatusChanged -= LiveRoomVM_ReconnectStatusChanged;
            NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
            Window.Current.CoreWindow.KeyDown -= CoreWindow_KeyDown;
            ApplicationView.GetForCurrentView().Consolidated -= LiveRoomPage_Consolidated;

            await StopPlayAsync();
            LogLiveRoomMemory("直播间播放与VM清理完成");
            DetachControlEvents();
            DetachMediaPlayerEvents();
            ClearControlReferences();
            LogLiveRoomMemory("直播间控件引用清理完成");
            try { player.SetMediaPlayer(null); } catch { }
            try { mediaPlayer?.Dispose(); } catch { }
            if (clearWindowContent)
            {
                ClearWindowContentReference();
            }
            liveRoomVM.ReleaseViewReferences();
            if (liveRoomWindowRegistered)
            {
                liveRoomWindowRegistered = false;
                MessageCenter.UnregisterLiveRoomWindow();
            }
        }

        private void CleanupLiveRoomPage()
        {
            _ = CleanupLiveRoomPageAsync();
        }

        private void DetachControlEvents()
        {
            if (controlEventsDetached)
            {
                return;
            }
            controlEventsDetached = true;
            liveRoomVM.PropertyChanged -= LiveRoomVM_PropertyChanged;
            GridRight.SizeChanged -= GridRight_SizeChanged;
            PlayDecoder.SelectionChanged -= PlayDecoder_SelectionChanged;
            cbDecoder.SelectionChanged -= CbDecoder_SelectionChanged;
            PlaySWDanmu.Toggled -= PlaySWDanmu_Toggled;
            swKeepSC.Toggled -= SwKeepSC_Toggled;
            SliderVolume.ValueChanged -= SliderVolume_ValueChanged;
            numCleanCount.Loaded -= NumCleanCount_Loaded;
            numCleanCount.ValueChanged -= NumCleanCount_ValueChanged;
            numFontsize.Loaded -= NumFontsize_Loaded;
            numFontsize.ValueChanged -= NumFontsize_ValueChanged;
            DanmuTopMargin.ValueChanged -= DanmuTopMargin_ValueChanged;
            DanmuSettingFontZoom.ValueChanged -= DanmuSettingFontZoom_ValueChanged;
            DanmuSettingSpeed.ValueChanged -= DanmuSettingSpeed_ValueChanged;
            DanmuSettingOpacity.ValueChanged -= DanmuSettingOpacity_ValueChanged;
            DanmuSettingBold.Toggled -= DanmuSettingBold_Toggled;
            DanmuSettingStyle.SelectionChanged -= DanmuSettingStyle_SelectionChanged;
            DanmuSettingArea.ValueChanged -= DanmuSettingArea_ValueChanged;
            DanmuSettingColourful.Toggled -= DanmuSettingColourful_Toggled;

            xboxSettingsDecoder.SelectionChanged -= XboxSettingsDecoder_SelectionChanged;
            xboxSettingsDMSize.SelectionChanged -= XboxSettingsDMSize_SelectionChanged;
            xboxSettingsDMSpeed.SelectionChanged -= XboxSettingsDMSpeed_SelectionChanged;
            xboxSettingsDMOpacity.SelectionChanged -= XboxSettingsDMOpacity_SelectionChanged;
            xboxSettingsDMBold.Toggled -= XboxSettingsDMBold_Toggled;
            xboxSettingsDMStyle.SelectionChanged -= XboxSettingsDMStyle_SelectionChanged;
            xboxSettingsDMArea.SelectionChanged -= XboxSettingsDMArea_SelectionChanged;
            xboxSettingsDMColorful.Toggled -= XboxSettingsDMColorful_Toggled;
        }

        private void ClearControlReferences()
        {
            try { Bindings.StopTracking(); } catch { }
            try { DataContext = null; } catch { }
            try { PageRoot.DataContext = null; } catch { }
            try { XBoxSplitView.Pane = null; } catch { }
            try { XBoxSplitView.Content = null; } catch { }
            try { LiveMessageList.ItemsSource = null; } catch { }
            try { LiveSuperChatList.ItemsSource = null; } catch { }
            try { LiveDanmuSettingListWords.ItemsSource = null; } catch { }
            try { XboxSuperChat.ItemsSource = null; } catch { }
            try { xboxSettingsQuality.ItemsSource = null; } catch { }
            try { xboxSettingsLine.ItemsSource = null; } catch { }
            try { xboxSettingsCodec.ItemsSource = null; } catch { }
            try { xboxSettingsDMOpacity.ItemsSource = null; } catch { }
            try { xboxSettingsDMSpeed.ItemsSource = null; } catch { }
            try { xboxSettingsDMSize.ItemsSource = null; } catch { }
            try { xboxSettingsDMArea.ItemsSource = null; } catch { }
            try { PlayQuality.ItemsSource = null; } catch { }
            try { PlayLine.ItemsSource = null; } catch { }
            try { PlayCodec.ItemsSource = null; } catch { }
            try { DanmuControl.ClearAll(); } catch { }
            try { player.Source = null; } catch { }
            try { Window.Current.SetTitleBar(null); } catch { }
        }

        private void ClearWindowContentReference()
        {
            if (windowContentCleared)
            {
                return;
            }
            windowContentCleared = true;

            try
            {
                if (Window.Current.Content is Frame frame && ReferenceEquals(frame.Content, this))
                {
                    frame.Content = null;
                    return;
                }
            }
            catch
            {
            }

            try
            {
                if (ReferenceEquals(Window.Current.Content, this))
                {
                    Window.Current.Content = null;
                }
            }
            catch
            {
            }
        }

        private void DetachMediaPlayerEvents()
        {
            if (mediaPlayerEventsDetached || mediaPlayer == null)
            {
                return;
            }
            mediaPlayerEventsDetached = true;
            mediaPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
            mediaPlayer.PlaybackSession.BufferingStarted -= PlaybackSession_BufferingStarted;
            mediaPlayer.PlaybackSession.BufferingProgressChanged -= PlaybackSession_BufferingProgressChanged;
            mediaPlayer.PlaybackSession.BufferingEnded -= PlaybackSession_BufferingEnded;
            mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
            mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
        }
        //private void btnBack_Click(object sender, RoutedEventArgs e)
        //{
        //    if (this.Frame.CanGoBack)
        //    {
        //        this.Frame.GoBack();
        //    }
        //}
        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {

            CleanupLiveRoomPage();

            base.OnNavigatingFrom(e);
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.NavigationMode == Windows.UI.Xaml.Navigation.NavigationMode.New)
            {
                if (!liveRoomWindowRegistered)
                {
                    MessageCenter.RegisterLiveRoomWindow();
                    liveRoomWindowRegistered = true;
                }

                pageArgs = e.Parameter as PageArgs;
                if (Utils.IsXbox)
                {
                    LoadSetting();
                    LoadXboxSetting();
                }
                else
                {
                    LoadSetting();
                }

                var siteInfo = MainVM.Sites.FirstOrDefault(x => x.LiveSite.Equals(pageArgs.Site));

                liveRoomVM.SiteLogo = siteInfo.Logo;
                liveRoomVM.SiteName = siteInfo.Name;

                var data = pageArgs.Data as LiveRoomItem;
                MessageCenter.ChangeTitle("", pageArgs.Site);

                liveRoomVM.LoadData(pageArgs.Site, data.RoomID);

                // 如果是XBOX，自动进入全屏
                if (Utils.IsXbox)
                {
                    SetFullScreen(true);
                }
            }
        }

        private async Task CaptureVideo()
        {
            try
            {
                string fileName = DateTime.Now.ToString("yyyyMMddHHmmss") + ".jpg";
                StorageFolder applicationFolder = KnownFolders.PicturesLibrary;
                StorageFolder folder = await applicationFolder.CreateFolderAsync("直播截图", CreationCollisionOption.OpenIfExists);
                StorageFile saveFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.OpenIfExists);
                RenderTargetBitmap bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(player);
                var pixelBuffer = await bitmap.GetPixelsAsync();
                using (var fileStream = await saveFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fileStream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Ignore,
                         (uint)bitmap.PixelWidth,
                         (uint)bitmap.PixelHeight,
                         DisplayInformation.GetForCurrentView().LogicalDpi,
                         DisplayInformation.GetForCurrentView().LogicalDpi,
                         pixelBuffer.ToArray());
                    await encoder.FlushAsync();
                }
                Utils.ShowMessageToast("截图已经保存至图片库");
            }
            catch (Exception)
            {
                Utils.ShowMessageToast("截图失败");
            }
        }



        private void LoadSetting()
        {
            //右侧宽度
            var width = SettingHelper.GetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, 280);
            ColumnRight.Width = new GridLength(width, GridUnitType.Pixel);
            GridRight.SizeChanged -= GridRight_SizeChanged;
            GridRight.SizeChanged += GridRight_SizeChanged;
            //软解视频
            //cbDecode.SelectedIndex= SettingHelper.GetValue<int>(SettingHelper.DECODE, 0);
            //switch (cbDecode.SelectedIndex)
            //{
            //    case 1:
            //        _config.VideoDecoderMode = VideoDecoderMode.ForceSystemDecoder;
            //        break;
            //    case 2:
            //        _config.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            //        break;
            //    default:
            //        _config.VideoDecoderMode = VideoDecoderMode.Automatic;
            //        break;
            //}
            //cbDecode.SelectedIndex = SettingHelper.GetValue<int>(SettingHelper.DECODE, 0);
            //cbDecode.Loaded += new RoutedEventHandler((sender, e) =>
            //{
            //    cbDecode.SelectionChanged += new SelectionChangedEventHandler((obj, args) =>
            //    {
            //        SettingHelper.SetValue(SettingHelper.DECODE, cbDecode.SelectedIndex);
            //        switch (cbDecode.SelectedIndex)
            //        {
            //            case 1:
            //                _config.VideoDecoderMode = VideoDecoderMode.ForceSystemDecoder;
            //                break;
            //            case 2:
            //                _config.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            //                break;
            //            default:
            //                _config.VideoDecoderMode = VideoDecoderMode.Automatic;
            //                break;
            //        }
            //        Utils.ShowMessageToast("更改清晰度或刷新后生效");
            //    });
            //});

            //swSoftwareDecode.Loaded += new RoutedEventHandler((sender, e) =>
            //{
            //    swSoftwareDecode.Toggled += new RoutedEventHandler((obj, args) =>
            //    {
            //        SettingHelper.SetValue(SettingHelper.SORTWARE_DECODING, swSoftwareDecode.IsOn);
            //        //if (mediaPlayer != null)
            //        //{
            //        //    mediaPlayer.EnableHardwareDecoding = !swSoftwareDecode.IsOn;
            //        //}

            //        Utils.ShowMessageToast("更改清晰度或刷新后生效");
            //    });
            //});
            LoadDecoderSetting();
            //弹幕开关
            var state = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false) ? Visibility.Visible : Visibility.Collapsed;
            DanmuControl.Visibility = state;
            PlaySWDanmu.IsOn = state == Visibility.Visible;
            PlaySWDanmu.Toggled -= PlaySWDanmu_Toggled;
            PlaySWDanmu.Toggled += PlaySWDanmu_Toggled;

            // 保留醒目留言
            var keepSC = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true);
            swKeepSC.IsOn = keepSC;
            liveRoomVM.SetSCTimer();
            swKeepSC.Toggled -= SwKeepSC_Toggled;
            swKeepSC.Toggled += SwKeepSC_Toggled;

            //音量
            var volume = SettingHelper.GetValue<double>(SettingHelper.PLAYER_VOLUME, 1.0);
            mediaPlayer.Volume = volume;
            SliderVolume.Value = volume;
            SliderVolume.ValueChanged -= SliderVolume_ValueChanged;
            SliderVolume.ValueChanged += SliderVolume_ValueChanged;
            //亮度
            _brightness = SettingHelper.GetValue<double>(SettingHelper.PLAYER_BRIGHTNESS, 0);
            BrightnessShield.Opacity = _brightness;

            //弹幕清理
            numCleanCount.Value = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, 200);
            numCleanCount.Loaded -= NumCleanCount_Loaded;
            numCleanCount.Loaded += NumCleanCount_Loaded;

            //互动文字大小
            numFontsize.Value = SettingHelper.GetValue<double>(SettingHelper.MESSAGE_FONTSIZE, 14.0);
            numFontsize.Loaded -= NumFontsize_Loaded;
            numFontsize.Loaded += NumFontsize_Loaded;


            //弹幕关键词
            LiveDanmuSettingListWords.ItemsSource = settingVM.ShieldWords;

            //弹幕顶部距离
            DanmuControl.Margin = new Thickness(0, SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.TOP_MARGIN, 0), 0, 0);
            DanmuTopMargin.Value = DanmuControl.Margin.Top;
            DanmuTopMargin.ValueChanged -= DanmuTopMargin_ValueChanged;
            DanmuTopMargin.ValueChanged += DanmuTopMargin_ValueChanged;
            //弹幕大小
            DanmuControl.DanmakuSizeZoom = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, 1);
            DanmuSettingFontZoom.ValueChanged -= DanmuSettingFontZoom_ValueChanged;
            DanmuSettingFontZoom.ValueChanged += DanmuSettingFontZoom_ValueChanged;
            //弹幕速度
            DanmuControl.DanmakuDuration = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.SPEED, 10);
            DanmuSettingSpeed.ValueChanged -= DanmuSettingSpeed_ValueChanged;
            DanmuSettingSpeed.ValueChanged += DanmuSettingSpeed_ValueChanged;

            //保留一位小数
            DanmuControl.Opacity = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.OPACITY, 1.0);
            DanmuSettingOpacity.ValueChanged -= DanmuSettingOpacity_ValueChanged;
            DanmuSettingOpacity.ValueChanged += DanmuSettingOpacity_ValueChanged;
            //弹幕加粗
            DanmuControl.DanmakuBold = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.BOLD, false);
            DanmuSettingBold.Toggled -= DanmuSettingBold_Toggled;
            DanmuSettingBold.Toggled += DanmuSettingBold_Toggled;
            //弹幕样式
            var danmuStyle = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, 2);
            if (danmuStyle > 2)
            {
                danmuStyle = 2;
            }
            DanmuControl.DanmakuStyle = (DanmakuBorderStyle)danmuStyle;
            DanmuSettingStyle.SelectionChanged -= DanmuSettingStyle_SelectionChanged;
            DanmuSettingStyle.SelectionChanged += DanmuSettingStyle_SelectionChanged;


            //弹幕显示区域
            DanmuControl.DanmakuArea = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.AREA, 1);
            DanmuSettingArea.ValueChanged -= DanmuSettingArea_ValueChanged;
            DanmuSettingArea.ValueChanged += DanmuSettingArea_ValueChanged;

            //彩色弹幕
            DanmuSettingColourful.IsOn = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, true);
            DanmuSettingColourful.Toggled -= DanmuSettingColourful_Toggled;
            DanmuSettingColourful.Toggled += DanmuSettingColourful_Toggled;
        }
        private void LoadDecoderSetting()
        {
            var decoder = GetEffectiveVideoDecoderIndex();
            SyncDecoderControls(decoder);

            PlayDecoder.SelectionChanged -= PlayDecoder_SelectionChanged;
            PlayDecoder.SelectionChanged += PlayDecoder_SelectionChanged;
            cbDecoder.SelectionChanged -= CbDecoder_SelectionChanged;
            cbDecoder.SelectionChanged += CbDecoder_SelectionChanged;
            xboxSettingsDecoder.SelectionChanged -= XboxSettingsDecoder_SelectionChanged;
            xboxSettingsDecoder.SelectionChanged += XboxSettingsDecoder_SelectionChanged;
        }

        private int GetGlobalVideoDecoderIndex()
        {
            return ClampVideoDecoderIndex(SettingHelper.GetValue<int>(SettingHelper.VIDEO_DECODER, DefaultVideoDecoderIndex));
        }

        private int GetEffectiveVideoDecoderIndex()
        {
            var globalDecoder = GetGlobalVideoDecoderIndex();
            var roomKey = GetRoomVideoDecoderSettingKey();
            if (string.IsNullOrEmpty(roomKey))
            {
                return globalDecoder;
            }

            return ClampVideoDecoderIndex(SettingHelper.GetValue<int>(roomKey, globalDecoder));
        }

        private string GetRoomVideoDecoderSettingKey()
        {
            var siteName = liveRoomVM?.SiteName;
            var roomId = liveRoomVM?.RoomID;
            if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(roomId))
            {
                return null;
            }

            return $"{SettingHelper.LIVE_ROOM_VIDEO_DECODER_PREFIX}:{siteName}:{roomId}";
        }

        private int ClampVideoDecoderIndex(int value)
        {
            return value >= 0 && value <= 2 ? value : DefaultVideoDecoderIndex;
        }

        private void SyncDecoderControls(int decoder)
        {
            decoder = ClampVideoDecoderIndex(decoder);
            updatingDecoderSelection = true;
            try
            {
                SetDecoderComboIndex(PlayDecoder, decoder);
                SetDecoderComboIndex(cbDecoder, decoder);
                SetDecoderComboIndex(xboxSettingsDecoder, decoder);
            }
            finally
            {
                updatingDecoderSelection = false;
            }
        }

        private void SetDecoderComboIndex(ComboBox comboBox, int decoder)
        {
            if (comboBox == null)
            {
                return;
            }

            decoder = ClampVideoDecoderIndex(decoder);
            if (comboBox.Items != null && comboBox.Items.Count > 0)
            {
                decoder = Math.Min(decoder, comboBox.Items.Count - 1);
            }
            if (comboBox.SelectedIndex != decoder)
            {
                comboBox.SelectedIndex = decoder;
            }
        }

        private void PlayDecoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyDecoderSelection(PlayDecoder);
        }

        private void CbDecoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyDecoderSelection(cbDecoder);
        }

        private void XboxSettingsDecoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyDecoderSelection(xboxSettingsDecoder);
        }

        private void ApplyDecoderSelection(ComboBox source)
        {
            if (updatingDecoderSelection || source == null || source.SelectedIndex < 0)
            {
                return;
            }

            var decoder = ClampVideoDecoderIndex(source.SelectedIndex);
            var roomKey = GetRoomVideoDecoderSettingKey();
            if (string.IsNullOrEmpty(roomKey))
            {
                SettingHelper.SetValue(SettingHelper.VIDEO_DECODER, decoder);
            }
            else
            {
                SettingHelper.SetValue(roomKey, decoder);
            }

            SyncDecoderControls(decoder);
            RestartCurrentLineAfterDecoderChange(decoder);
        }

        private void RestartCurrentLineAfterDecoderChange(int decoder)
        {
            var url = liveRoomVM?.CurrentLine?.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                Utils.ShowMessageToast($"已保存{GetShortDecoderText(decoder)}");
                return;
            }

            LogHelper.Log($"直播间解码器切换 roomId={liveRoomVM?.RoomID} decoder={GetDecoderLogText(decoder)}，重载当前线路", LogType.DEBUG);
            Utils.ShowMessageToast($"已切换{GetShortDecoderText(decoder)}，正在重载当前线路");
            QueueSetPlayer(url, "DecoderChanged");
        }

        private string GetShortDecoderText(int decoder)
        {
            switch (ClampVideoDecoderIndex(decoder))
            {
                case 1:
                    return "硬解";
                case 2:
                    return "软解";
                default:
                    return "自动解码";
            }
        }

        private string GetDecoderLogText(int decoder)
        {
            switch (ClampVideoDecoderIndex(decoder))
            {
                case 1:
                    return "ForceSystemDecoder";
                case 2:
                    return "ForceFFmpegSoftwareDecoder";
                default:
                    return "Automatic";
            }
        }

        private void PlaySWDanmu_Toggled(object sender, RoutedEventArgs e)
        {
            var visibility = PlaySWDanmu.IsOn ? Visibility.Visible : Visibility.Collapsed;
            DanmuControl.Visibility = visibility;
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHOW, PlaySWDanmu.IsOn);
        }

        private void SwKeepSC_Toggled(object sender, RoutedEventArgs e)
        {
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, swKeepSC.IsOn);
            liveRoomVM.SetSCTimer();
        }

        private void SliderVolume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            mediaPlayer.Volume = SliderVolume.Value;
            SettingHelper.SetValue<double>(SettingHelper.PLAYER_VOLUME, SliderVolume.Value);
        }

        private void NumCleanCount_Loaded(object sender, RoutedEventArgs e)
        {
            numCleanCount.ValueChanged -= NumCleanCount_ValueChanged;
            numCleanCount.ValueChanged += NumCleanCount_ValueChanged;
        }

        private void NumCleanCount_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (double.IsNaN(e.NewValue))
            {
                return;
            }
            liveRoomVM.MessageCleanCount = Convert.ToInt32(e.NewValue);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, Convert.ToInt32(e.NewValue));
        }

        private void NumFontsize_Loaded(object sender, RoutedEventArgs e)
        {
            numFontsize.ValueChanged -= NumFontsize_ValueChanged;
            numFontsize.ValueChanged += NumFontsize_ValueChanged;
        }

        private void NumFontsize_ValueChanged(object sender, NumberBoxValueChangedEventArgs e)
        {
            if (double.IsNaN(e.NewValue))
            {
                return;
            }
            SettingHelper.SetValue(SettingHelper.MESSAGE_FONTSIZE, e.NewValue);
        }

        private void DanmuTopMargin_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.TOP_MARGIN, DanmuTopMargin.Value);
            DanmuControl.Margin = new Thickness(0, DanmuTopMargin.Value, 0, 0);
        }

        private void DanmuSettingFontZoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isMini) return;
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, DanmuSettingFontZoom.Value);
        }

        private void DanmuSettingSpeed_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (isMini) return;
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.SPEED, DanmuSettingSpeed.Value);
        }

        private void DanmuSettingOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.OPACITY, DanmuSettingOpacity.Value);
        }

        private void DanmuSettingBold_Toggled(object sender, RoutedEventArgs e)
        {
            SettingHelper.SetValue<bool>(SettingHelper.LiveDanmaku.BOLD, DanmuSettingBold.IsOn);
        }

        private void DanmuSettingStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DanmuSettingStyle.SelectedIndex != -1)
            {
                SettingHelper.SetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, DanmuSettingStyle.SelectedIndex);
            }
        }

        private void DanmuSettingArea_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.AREA, DanmuSettingArea.Value);
        }

        private void DanmuSettingColourful_Toggled(object sender, RoutedEventArgs e)
        {
            SettingHelper.SetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, DanmuSettingColourful.IsOn);
        }

        private void GridRight_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0)
            {
                return;
            }
            SettingHelper.SetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, e.NewSize.Width + 16);
        }
        private void LoadXboxSetting()
        {

            LoadDecoderSetting();

            //弹幕开关
            var state = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false) ? Visibility.Visible : Visibility.Collapsed;

            PlaySWDanmu.IsOn = state == Visibility.Visible;
            PlaySWDanmu.Toggled -= PlaySWDanmu_Toggled;
            PlaySWDanmu.Toggled += PlaySWDanmu_Toggled;

            ////音量
            var volume = SettingHelper.GetValue<double>(SettingHelper.PLAYER_VOLUME, 1.0);
            SliderVolume.Value = volume;
            SliderVolume.ValueChanged -= SliderVolume_ValueChanged;
            SliderVolume.ValueChanged += SliderVolume_ValueChanged;


            //弹幕关键词
            LiveDanmuSettingListWords.ItemsSource = settingVM.ShieldWords;

            //弹幕大小
            //DanmuControl.DanmakuSizeZoom = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, 1);
            xboxSettingsDMSize.SelectionChanged -= XboxSettingsDMSize_SelectionChanged;
            xboxSettingsDMSize.SelectionChanged += XboxSettingsDMSize_SelectionChanged;

            //弹幕速度
            //DanmuControl.DanmakuDuration = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.SPEED, 10);
            xboxSettingsDMSpeed.SelectionChanged -= XboxSettingsDMSpeed_SelectionChanged;
            xboxSettingsDMSpeed.SelectionChanged += XboxSettingsDMSpeed_SelectionChanged;

            //弹幕透明度
            //DanmuControl.Opacity = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.OPACITY, 1.0);
            xboxSettingsDMOpacity.SelectionChanged -= XboxSettingsDMOpacity_SelectionChanged;
            xboxSettingsDMOpacity.SelectionChanged += XboxSettingsDMOpacity_SelectionChanged;


            //弹幕加粗
            //DanmuControl.DanmakuBold = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.BOLD, false);
            xboxSettingsDMBold.Toggled -= XboxSettingsDMBold_Toggled;
            xboxSettingsDMBold.Toggled += XboxSettingsDMBold_Toggled;

            //弹幕样式
            var danmuStyle = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, 2);
            if (danmuStyle > 2)
            {
                danmuStyle = 2;
            }
            //DanmuControl.DanmakuStyle = (DanmakuBorderStyle)danmuStyle;
            xboxSettingsDMStyle.SelectionChanged -= XboxSettingsDMStyle_SelectionChanged;
            xboxSettingsDMStyle.SelectionChanged += XboxSettingsDMStyle_SelectionChanged;


            //弹幕显示区域
            //DanmuControl.DanmakuArea = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.AREA, 1);
            xboxSettingsDMArea.SelectionChanged -= XboxSettingsDMArea_SelectionChanged;
            xboxSettingsDMArea.SelectionChanged += XboxSettingsDMArea_SelectionChanged;

            //彩色弹幕
            xboxSettingsDMColorful.IsOn = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, true);
            xboxSettingsDMColorful.Toggled -= XboxSettingsDMColorful_Toggled;
            xboxSettingsDMColorful.Toggled += XboxSettingsDMColorful_Toggled;

        }

        private void XboxSettingsDMSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (xboxSettingsDMSize.SelectedValue == null)
            {
                return;
            }
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, (double)xboxSettingsDMSize.SelectedValue);
        }

        private void XboxSettingsDMSpeed_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (xboxSettingsDMSpeed.SelectedValue == null)
            {
                return;
            }
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.SPEED, (int)xboxSettingsDMSpeed.SelectedValue);
        }

        private void XboxSettingsDMOpacity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (xboxSettingsDMOpacity.SelectedValue == null)
            {
                return;
            }
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.OPACITY, (double)xboxSettingsDMOpacity.SelectedValue);
        }

        private void XboxSettingsDMBold_Toggled(object sender, RoutedEventArgs e)
        {
            SettingHelper.SetValue<bool>(SettingHelper.LiveDanmaku.BOLD, xboxSettingsDMBold.IsOn);
        }

        private void XboxSettingsDMStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (xboxSettingsDMStyle.SelectedIndex != -1)
            {
                SettingHelper.SetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, xboxSettingsDMStyle.SelectedIndex);
            }
        }

        private void XboxSettingsDMArea_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (xboxSettingsDMArea.SelectedValue == null)
            {
                return;
            }
            SettingHelper.SetValue<double>(SettingHelper.LiveDanmaku.AREA, (double)xboxSettingsDMArea.SelectedValue);
        }

        private void XboxSettingsDMColorful_Toggled(object sender, RoutedEventArgs e)
        {
            SettingHelper.SetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, xboxSettingsDMColorful.IsOn);
        }
        private void RemoveLiveDanmuWord_Click(object sender, RoutedEventArgs e)
        {
            var word = (sender as AppBarButton).DataContext as string;
            settingVM.ShieldWords.Remove(word);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHIELD_WORD, JsonConvert.SerializeObject(settingVM.ShieldWords));
        }

        private void LiveDanmuSettingTxtWord_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (string.IsNullOrEmpty(LiveDanmuSettingTxtWord.Text))
            {
                Utils.ShowMessageToast("关键字不能为空");
                return;
            }
            if (!settingVM.ShieldWords.Contains(LiveDanmuSettingTxtWord.Text))
            {
                settingVM.ShieldWords.Add(LiveDanmuSettingTxtWord.Text);
                SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHIELD_WORD, JsonConvert.SerializeObject(settingVM.ShieldWords));
            }

            LiveDanmuSettingTxtWord.Text = "";
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHIELD_WORD, JsonConvert.SerializeObject(settingVM.ShieldWords));
        }


        #region 手势
        int showControlsFlag = 0;
        bool pointer_in_player = false;

        private void Grid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowControl(control.Visibility == Visibility.Collapsed);

        }
        bool runing = false;
        private async void ShowControl(bool show)
        {
            if (runing) return;
            runing = true;
            if (show)
            {
                showControlsFlag = 0;
                control.Visibility = Visibility.Visible;

                await control.FadeInAsync(280);

            }
            else
            {
                if (pointer_in_player)
                {
                    Window.Current.CoreWindow.PointerCursor = null;
                }
                await control.FadeOutAsync(280);
                control.Visibility = Visibility.Collapsed;
            }
            runing = false;
        }
        private void Grid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (isMini)
            {
                MiniWidnows(false);
                return;
            }

            if (PlayBtnFullScreen.Visibility == Visibility.Visible)
            {
                PlayBtnFullScreen_Click(sender, null);
            }
            else
            {

                PlayBtnExitFullScreen_Click(sender, null);
            }
        }
        private void Grid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            pointer_in_player = true;
        }

        private void Grid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            pointer_in_player = false;
            Window.Current.CoreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Arrow, 0);
        }

        private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Window.Current.CoreWindow.PointerCursor == null)
            {
                Window.Current.CoreWindow.PointerCursor = new Windows.UI.Core.CoreCursor(Windows.UI.Core.CoreCursorType.Arrow, 0);
            }

        }

        bool ManipulatingBrightness = false;
        private void Grid_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            e.Handled = true;
            //progress.Visibility = Visibility.Visible;
            if (ManipulatingBrightness)
                HandleSlideBrightnessDelta(e.Delta.Translation.Y);
            else
                HandleSlideVolumeDelta(e.Delta.Translation.Y);
        }


        private void HandleSlideVolumeDelta(double delta)
        {
            if (delta > 0)
            {
                double dd = delta / (this.ActualHeight * 0.8);

                //slider_V.Value -= d;
                var volume = mediaPlayer.Volume - dd;
                if (volume < 0) volume = 0;
                SliderVolume.Value = volume;

            }
            else
            {
                double dd = Math.Abs(delta) / (this.ActualHeight * 0.8);
                var volume = mediaPlayer.Volume + dd;
                if (volume > 1) volume = 1;
                SliderVolume.Value = volume;
                //slider_V.Value += d;
            }
            TxtToolTip.Text = "音量:" + mediaPlayer.Volume.ToString("P");

            //Utils.ShowMessageToast("音量:" +  mediaElement.MediaPlayer.Volume.ToString("P"), 3000);
        }
        private void HandleSlideBrightnessDelta(double delta)
        {
            double dd = Math.Abs(delta) / (this.ActualHeight * 0.8);
            if (delta > 0)
            {
                Brightness = Math.Min(Brightness + dd, 1);
            }
            else
            {
                Brightness = Math.Max(Brightness - dd, 0);
            }
            TxtToolTip.Text = "亮度:" + Math.Abs(Brightness - 1).ToString("P");
        }
        private void Grid_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            e.Handled = true;
            TxtToolTip.Text = "";
            ToolTip.Visibility = Visibility.Visible;

            if (e.Position.X < this.ActualWidth / 2)
                ManipulatingBrightness = true;
            else
                ManipulatingBrightness = false;

        }

        double _brightness;
        double Brightness
        {
            get => _brightness;
            set
            {
                _brightness = value;
                BrightnessShield.Opacity = value;
                SettingHelper.SetValue<double>(SettingHelper.PLAYER_BRIGHTNESS, _brightness);
            }
        }

        private void Grid_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            e.Handled = true;
            ToolTip.Visibility = Visibility.Collapsed;
        }
        #endregion
        #region 窗口操作
        private void PlayBtnFullScreen_Click(object sender, RoutedEventArgs e)
        {
            SetFullScreen(true);
        }

        private void PlayBtnExitFullScreen_Click(object sender, RoutedEventArgs e)
        {
            SetFullScreen(false);
        }

        private void PlayBtnExitFullWindow_Click(object sender, RoutedEventArgs e)
        {
            SetFullWindow(false);
        }

        private void PlayBtnFullWindow_Click(object sender, RoutedEventArgs e)
        {
            SetFullWindow(true);
        }

        private void PlayBtnMinWindow_Click(object sender, RoutedEventArgs e)
        {
            MiniWidnows(true);
        }
        private void SetFullWindow(bool e)
        {

            if (e)
            {
                PlayBtnFullWindow.Visibility = Visibility.Collapsed;
                PlayBtnExitFullWindow.Visibility = Visibility.Visible;
                ColumnRight.Width = new GridLength(0, GridUnitType.Pixel);
                ColumnRight.MinWidth = 0;
                BottomInfo.Height = new GridLength(0, GridUnitType.Pixel);
            }
            else
            {
                PlayBtnFullWindow.Visibility = Visibility.Visible;
                PlayBtnExitFullWindow.Visibility = Visibility.Collapsed;
                ColumnRight.Width = new GridLength(SettingHelper.GetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, 280), GridUnitType.Pixel);
                ColumnRight.MinWidth = 100;
                BottomInfo.Height = GridLength.Auto;
            }
        }
        private void SetFullScreen(bool e)
        {
            ApplicationView view = ApplicationView.GetForCurrentView();
            HideTitleBar(e);
            if (e)
            {

                PlayBtnFullScreen.Visibility = Visibility.Collapsed;
                PlayBtnExitFullScreen.Visibility = Visibility.Visible;

                ColumnRight.Width = new GridLength(0, GridUnitType.Pixel);
                ColumnRight.MinWidth = 0;

                BottomInfo.Height = new GridLength(0, GridUnitType.Pixel);
                //全屏
                if (!view.IsFullScreenMode)
                {
                    view.TryEnterFullScreenMode();
                }
            }
            else
            {
                PlayBtnFullScreen.Visibility = Visibility.Visible;
                PlayBtnExitFullScreen.Visibility = Visibility.Collapsed;
                var width = SettingHelper.GetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, 280);
                ColumnRight.Width = new GridLength(width, GridUnitType.Pixel);
                //ColumnRight.Width = new GridLength(280, GridUnitType.Pixel);
                ColumnRight.MinWidth = 100;
                BottomInfo.Height = GridLength.Auto;
                //退出全屏
                if (view.IsFullScreenMode)
                {
                    view.ExitFullScreenMode();
                }
            }
        }
        private async void MiniWidnows(bool mini)
        {
            HideTitleBar(mini);
            isMini = mini;
            ApplicationView view = ApplicationView.GetForCurrentView();
            if (mini)
            {
                SetFullWindow(true);
                if (Utils.IsXbox && SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0) == 0)
                {
                    XBoxControl.Visibility = Visibility.Collapsed;
                }
                else
                {
                    StandardControl.Visibility = Visibility.Collapsed;
                }

                MiniControl.Visibility = Visibility.Visible;

                if (ApplicationView.GetForCurrentView().IsViewModeSupported(ApplicationViewMode.CompactOverlay))
                {
                    await ApplicationView.GetForCurrentView().TryEnterViewModeAsync(ApplicationViewMode.CompactOverlay);
                    DanmuControl.DanmakuSizeZoom = 0.5;
                    DanmuControl.DanmakuDuration = 6;
                    DanmuControl.ClearAll();
                }
            }
            else
            {
                SetFullWindow(false);
                if (Utils.IsXbox && SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0) == 0)
                {
                    XBoxControl.Visibility = Visibility.Visible;
                }
                else
                {
                    StandardControl.Visibility = Visibility.Visible;
                }

                MiniControl.Visibility = Visibility.Collapsed;
                await ApplicationView.GetForCurrentView().TryEnterViewModeAsync(ApplicationViewMode.Default);
                DanmuControl.DanmakuSizeZoom = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, 1);
                DanmuControl.DanmakuDuration = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.SPEED, 10);
                DanmuControl.ClearAll();
                DanmuControl.Visibility = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false) ? Visibility.Visible : Visibility.Collapsed;
            }

        }
        private void BottomBtnExitMiniWindows_Click(object sender, RoutedEventArgs e)
        {
            MiniWidnows(false);
        }

        private async void PlayTopBtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            await CaptureVideo();
        }

        private void PlayBtnPlay_Click(object sender, RoutedEventArgs e)
        {
            mediaPlayer.Play();
        }

        private void PlayBtnPause_Click(object sender, RoutedEventArgs e)
        {
            mediaPlayer.Pause();
        }


        #endregion

        private void BottomBtnShare_Click(object sender, RoutedEventArgs e)
        {
            if (liveRoomVM.detail == null)
            {
                return;
            }
            Utils.SetClipboard(liveRoomVM.detail.Url);
            Utils.ShowMessageToast("已复制链接到剪切板");
        }

        private async void BottomBtnOpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (liveRoomVM.detail == null)
            {
                return;
            }
            await Windows.System.Launcher.LaunchUriAsync(new Uri(liveRoomVM.detail.Url));
        }

        private void BottomBtnPlayUrl_Click(object sender, RoutedEventArgs e)
        {
            if (liveRoomVM.CurrentLine == null)
            {
                return;
            }
            Utils.SetClipboard(liveRoomVM.CurrentLine.Url);
            Utils.ShowMessageToast("已复制链接到剪切板");
        }

        private async void BottomBtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (liveRoomVM.Loading) return;
            currentLineRetryUrl = null;
            currentLineRetryCount = 0;
            ResetStreamReconnectState();
            StopBufferingTimer();
            if (mediaPlayer != null)
            {
                mediaPlayer.Pause();
                mediaPlayer.Source = null;
            }
            if (interopMSS != null)
            {
                interopMSS.Dispose();
                interopMSS = null;
            }

            await liveRoomVM.StopAsync();
            liveRoomVM.LoadData(pageArgs.Site, liveRoomVM.RoomID);
        }

        private void XboxSuperChat_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as SuperChatItem;
            ContentDialog dialog = new ContentDialog
            {
                Title = item.UserName,
                Content = new TextBlock
                {
                    Text = item.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 20
                },
                IsPrimaryButtonEnabled = true,
                IsSecondaryButtonEnabled = false,
                PrimaryButtonText = "确定"
            };
            _ = dialog.ShowAsync();
        }
    }
}
