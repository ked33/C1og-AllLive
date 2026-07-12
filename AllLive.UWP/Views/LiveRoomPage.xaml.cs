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
using Windows.Web;
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
        private static readonly TimeSpan BilibiliDirectFlvCreateSourceTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan BilibiliLocalProxyCreateSourceTimeout = TimeSpan.FromSeconds(3);
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
        private int playbackDiagnosticProbeCount;
        private DateTimeOffset? currentBufferingStartedUtc;
        private DateTimeOffset? lastUiHeartbeatUtc;
        private double maxUiHeartbeatDelayMsSinceAttempt;
        private int uiHeartbeatLateCountSinceAttempt;
        private readonly object playbackDiagnosticsLock = new object();
        private readonly Queue<string> playbackSampleHistory = new Queue<string>();
        private readonly Queue<string> playbackEventHistory = new Queue<string>();
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
        private static readonly TimeSpan PlaybackSlowWallThreshold = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan UiHeartbeatDelayLogThreshold = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan ProbeReadTimeout = TimeSpan.FromSeconds(6);
        private const double PlaybackSlowRatioThreshold = 0.85;
        private const double PlaybackStallRatioThreshold = 0.20;
        private const int DefaultVideoDecoderIndex = 1;
        private const int ExperimentalD3D11VideoDecoderIndex = 3;
        private const int MaxPlaybackSampleHistory = 8;
        private const int MaxPlaybackEventHistory = 24;
        private const int MaxPlaybackDiagnosticProbeCount = 2;
        private const uint UrlFirstBytesProbeBytes = 32;
        private const uint FlvDiagnosticProbeBytes = 1048576;
        private const uint ThroughputProbeBytes = 1048576;
        private const uint ConnectivityProbeBytes = 4096;
        private const string ConnectivityProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";
        private bool updatingDecoderSelection;

        private sealed class PlaybackSampleResult
        {
            public string Text { get; set; }
            public bool Suspicious { get; set; }
            public bool Slow { get; set; }
            public string Reason { get; set; }
            public string Url { get; set; }
        }

        private sealed class ProbeReadResult
        {
            public byte[] Buffer { get; set; }
            public uint BytesRead { get; set; }
            public long FirstByteMs { get; set; } = -1;
            public long ElapsedMs { get; set; }
            public bool TimedOut { get; set; }
            public string Error { get; set; }
        }

        private static bool IsDebugDiagnosticsEnabled()
        {
            return LogHelper.Enabled;
        }

        private static void LogDebugIfEnabled(string message)
        {
            if (!IsDebugDiagnosticsEnabled() || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            LogHelper.Log(message, LogType.DEBUG);
        }

        private static void LogDebugIfEnabled(Func<string> messageFactory)
        {
            if (!IsDebugDiagnosticsEnabled() || messageFactory == null)
            {
                return;
            }

            var message = messageFactory();
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            LogHelper.Log(message, LogType.DEBUG);
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
                if (IsDebugDiagnosticsEnabled())
                {
                    AddPlaybackEventHistory("MediaEnded", sender?.PlaybackSession);
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
                AddPlaybackEventHistory("BufferingEnded", sender);
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
                AddPlaybackEventHistory("BufferingStarted", sender);
                LogBufferingStarted(sender);
                StartBufferingTimer();
            });
        }

        private async void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (IsDebugDiagnosticsEnabled())
            {
                await EnsureDiagnosticsSnapshotAsync();
            }
            await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (IsDebugDiagnosticsEnabled())
                {
                    AddPlaybackEventHistory($"MediaFailed/{args.Error}", sender?.PlaybackSession);
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
                }

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
                if (IsDebugDiagnosticsEnabled())
                {
                    AddPlaybackEventHistory("MediaOpened", sender?.PlaybackSession);
                }
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
                    if (IsDebugDiagnosticsEnabled())
                    {
                        AddPlaybackEventHistory($"StateChanged/{sender.PlaybackState}", sender);
                        var sessionSnapshot = BuildPlaybackSessionSnapshot(sender);
                        if (!string.IsNullOrEmpty(sessionSnapshot))
                        {
                            LogHelper.Log($"播放状态变更: {sender.PlaybackState}\n{sessionSnapshot}", LogType.DEBUG);
                        }
                    }
                    if (sender.PlaybackState == MediaPlaybackState.Playing)
                    {
                        lastPlaybackStartUtc = DateTimeOffset.UtcNow;
                        lastPlaybackUrl = liveRoomVM?.CurrentLine?.Url;
                        if (IsDebugDiagnosticsEnabled())
                        {
                            LogPlaybackMediaInfo("Playing");
                            StartPlaybackSampling(System.Threading.Volatile.Read(ref mediaSourceAttemptVersion));
                        }
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
                        liveRoomVM?.StartDeferredLiveExtras();
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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

            currentBufferingStartedUtc = DateTimeOffset.UtcNow;
            var snapshot = BuildPlaybackSessionSnapshot(session);
            LogHelper.Log(JoinNonEmpty(
                "BufferingStarted",
                BuildSafePlaybackContext(),
                snapshot), LogType.DEBUG);
        }

        private void LogBufferingEnded(MediaPlaybackSession session)
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            if (!IsDebugDiagnosticsEnabled() ||
                attemptVersion <= 0 ||
                !IsMediaSourceAttemptCurrent(attemptVersion))
            {
                return;
            }

            CancelPlaybackSampling();
            playbackDiagnosticProbeCount = 0;
            lock (playbackDiagnosticsLock)
            {
                playbackSampleHistory.Clear();
            }
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
                    IsDebugDiagnosticsEnabled() &&
                    IsMediaSourceAttemptCurrent(attemptVersion) &&
                    DateTimeOffset.UtcNow - startedUtc < PlaybackSampleDuration)
                {
                    await Task.Delay(PlaybackSampleInterval, token);
                    if (token.IsCancellationRequested ||
                        !IsDebugDiagnosticsEnabled() ||
                        !IsMediaSourceAttemptCurrent(attemptVersion))
                    {
                        return;
                    }

                    PlaybackSampleResult sample = null;
                    var dispatchQueuedUtc = DateTimeOffset.UtcNow;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (token.IsCancellationRequested ||
                            !IsDebugDiagnosticsEnabled() ||
                            !IsMediaSourceAttemptCurrent(attemptVersion))
                        {
                            return;
                        }
                        var now = DateTimeOffset.UtcNow;
                        var uiDispatchDelay = now - dispatchQueuedUtc;
                        sampleIndex++;
                        sample = BuildPlaybackSample(sampleIndex, now, previousUtc, previousPosition, uiDispatchDelay, attemptVersion);
                        if (sample != null && !string.IsNullOrEmpty(sample.Text))
                        {
                            AddPlaybackSampleHistory(sample.Text);
                            LogHelper.Log(sample.Text, LogType.DEBUG);
                        }
                        previousUtc = now;
                        previousPosition = TryReadPlaybackPosition(mediaPlayer?.PlaybackSession);
                    });

                    if (sample != null &&
                        (sample.Suspicious || sample.Slow) &&
                        playbackDiagnosticProbeCount < MaxPlaybackDiagnosticProbeCount)
                    {
                        playbackDiagnosticProbeCount++;
                        StartPlaybackStallProbe(sample.Url, sample.Text, sample.Reason, attemptVersion);
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogDebugIfEnabled(() => $"播放采样异常: {BuildExceptionSummary(ex)}");
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

        private PlaybackSampleResult BuildPlaybackSample(int sampleIndex, DateTimeOffset now, DateTimeOffset? previousUtc, TimeSpan? previousPosition, TimeSpan uiDispatchDelay, int attemptVersion)
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
            double? paceRatio = null;
            if (wallDelta.HasValue &&
                wallDelta.Value.TotalMilliseconds > 0 &&
                positionDelta.HasValue)
            {
                paceRatio = positionDelta.Value.TotalMilliseconds / wallDelta.Value.TotalMilliseconds;
            }
            var canClassifyPace = state == MediaPlaybackState.Playing &&
                wallDelta.HasValue &&
                wallDelta.Value >= PlaybackSlowWallThreshold;
            var suspicious = canClassifyPace &&
                (!positionDelta.HasValue ||
                 positionDelta.Value.TotalSeconds < 0.3 ||
                 positionDelta.Value < TimeSpan.Zero ||
                 (paceRatio.HasValue && paceRatio.Value < PlaybackStallRatioThreshold));
            var slow = canClassifyPace &&
                !suspicious &&
                (!paceRatio.HasValue || paceRatio.Value < PlaybackSlowRatioThreshold);
            var reason = suspicious ? "SevereStall" : slow ? "SlowProgress" : "Normal";

            var sb = new StringBuilder();
            sb.AppendLine($"播放采样 #{sampleIndex} attempt={attemptVersion}");
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
            if (paceRatio.HasValue)
            {
                sb.AppendLine($"ProgressPaceRatio: {paceRatio.Value:F3}");
            }
            sb.AppendLine($"UiDispatchDelayMs: {uiDispatchDelay.TotalMilliseconds:F0}");
            sb.AppendLine($"疑似进度慢推进: {slow}");
            sb.AppendLine($"疑似进度停滞: {suspicious}");
            AppendPlaybackValue(sb, "BufferingProgress", () => session.BufferingProgress.ToString("P"));
            AppendPlaybackValue(sb, "DownloadProgress", () => session.DownloadProgress.ToString("P"));
            AppendPlaybackValue(sb, "PlaybackRate", () => session.PlaybackRate.ToString());
            var uiHealth = BuildUiHealthSnapshot(now);
            if (!string.IsNullOrEmpty(uiHealth))
            {
                sb.AppendLine(uiHealth);
            }
            var resource = BuildRuntimeResourceSnapshot();
            if (!string.IsNullOrEmpty(resource))
            {
                sb.AppendLine(resource);
            }
            var urlSummary = BuildUrlSummary(liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl);
            if (!string.IsNullOrEmpty(urlSummary))
            {
                sb.AppendLine(urlSummary);
            }

            return new PlaybackSampleResult
            {
                Text = sb.ToString().TrimEnd(),
                Suspicious = suspicious,
                Slow = slow,
                Reason = reason,
                Url = liveRoomVM?.CurrentLine?.Url ?? lastPlaybackUrl
            };
        }

        private void StartPlaybackStallProbe(string url, string sampleText, string reason, int attemptVersion)
        {
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                if (!IsDebugDiagnosticsEnabled())
                {
                    return;
                }

                var probe = await ProbeUrlAsync(url);
                var throughputProbe = await ProbeThroughputAsync(url);
                var flvProbe = await ProbeFlvAsync(url);
                var connectivityProbe = await ProbeConnectivityBaselineAsync();
                var correlation = BuildProbeCorrelationSummary(probe, throughputProbe, flvProbe, connectivityProbe);
                if (!IsDebugDiagnosticsEnabled())
                {
                    return;
                }
                var merged = JoinNonEmpty(
                    $"播放卡顿诊断 reason={reason} attempt={attemptVersion}",
                    lastMediaInfoSnapshot,
                    sampleText,
                    BuildRecentPlaybackSamplesSnapshot(),
                    BuildRecentPlaybackEventsSnapshot(),
                    BuildUiHealthSnapshot(DateTimeOffset.UtcNow),
                    BuildRuntimeResourceSnapshot(),
                    BuildNetworkStateSnapshot(),
                    correlation,
                    probe,
                    throughputProbe,
                    flvProbe,
                    connectivityProbe);
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
            playbackDiagnosticProbeCount = 0;
            currentBufferingStartedUtc = null;
            lastMediaInfoSnapshot = null;
            lastUiHeartbeatUtc = DateTimeOffset.UtcNow;
            maxUiHeartbeatDelayMsSinceAttempt = 0;
            uiHeartbeatLateCountSinceAttempt = 0;
            lock (playbackDiagnosticsLock)
            {
                playbackSampleHistory.Clear();
                playbackEventHistory.Clear();
            }
        }

        private void AddPlaybackSampleHistory(string sample)
        {
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrWhiteSpace(sample))
            {
                return;
            }

            lock (playbackDiagnosticsLock)
            {
                playbackSampleHistory.Enqueue(sample);
                while (playbackSampleHistory.Count > MaxPlaybackSampleHistory)
                {
                    playbackSampleHistory.Dequeue();
                }
            }
        }

        private string BuildRecentPlaybackSamplesSnapshot()
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return null;
            }

            List<string> samples;
            lock (playbackDiagnosticsLock)
            {
                if (playbackSampleHistory.Count == 0)
                {
                    return null;
                }
                samples = playbackSampleHistory.ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine("最近播放采样:");
            foreach (var sample in samples)
            {
                sb.AppendLine(sample);
            }
            return sb.ToString().TrimEnd();
        }

        private void AddPlaybackEventHistory(string eventName, MediaPlaybackSession session = null)
        {
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append($"{DateTimeOffset.UtcNow:O} attempt={System.Threading.Volatile.Read(ref mediaSourceAttemptVersion)} {eventName}");
            var state = TryReadPlaybackState(session ?? mediaPlayer?.PlaybackSession);
            var position = TryReadPlaybackPosition(session ?? mediaPlayer?.PlaybackSession);
            if (state.HasValue)
            {
                sb.Append($" state={state.Value}");
            }
            if (position.HasValue)
            {
                sb.Append($" position={position.Value}");
            }
            if (!string.IsNullOrEmpty(liveRoomVM?.CurrentLine?.Name))
            {
                sb.Append($" line={liveRoomVM.CurrentLine.Name}");
            }

            lock (playbackDiagnosticsLock)
            {
                playbackEventHistory.Enqueue(sb.ToString());
                while (playbackEventHistory.Count > MaxPlaybackEventHistory)
                {
                    playbackEventHistory.Dequeue();
                }
            }
        }

        private string BuildRecentPlaybackEventsSnapshot()
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return null;
            }

            List<string> events;
            lock (playbackDiagnosticsLock)
            {
                if (playbackEventHistory.Count == 0)
                {
                    return null;
                }
                events = playbackEventHistory.ToList();
            }

            var sb = new StringBuilder();
            sb.AppendLine("最近播放事件:");
            foreach (var item in events)
            {
                sb.AppendLine(item);
            }
            return sb.ToString().TrimEnd();
        }

        private void UpdateUiHeartbeat(string source)
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (lastUiHeartbeatUtc.HasValue)
            {
                var delta = now - lastUiHeartbeatUtc.Value;
                var deltaMs = delta.TotalMilliseconds;
                if (deltaMs > maxUiHeartbeatDelayMsSinceAttempt)
                {
                    maxUiHeartbeatDelayMsSinceAttempt = deltaMs;
                }

                if (delta >= UiHeartbeatDelayLogThreshold)
                {
                    uiHeartbeatLateCountSinceAttempt++;
                    var state = mediaPlayer?.PlaybackSession?.PlaybackState;
                    if (state == MediaPlaybackState.Opening ||
                        state == MediaPlaybackState.Buffering ||
                        state == MediaPlaybackState.Playing)
                    {
                        LogHelper.Log(JoinNonEmpty(
                            $"UI线程心跳延迟 source={source} deltaMs={deltaMs:F0}",
                            BuildSafePlaybackContext(),
                            BuildUiHealthSnapshot(now),
                            BuildRuntimeResourceSnapshot()), LogType.DEBUG);
                    }
                }
            }
            lastUiHeartbeatUtc = now;
        }

        private string BuildUiHealthSnapshot(DateTimeOffset now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("UI线程:");
            if (lastUiHeartbeatUtc.HasValue)
            {
                sb.AppendLine($"LastHeartbeatAgeMs: {(now - lastUiHeartbeatUtc.Value).TotalMilliseconds:F0}");
            }
            else
            {
                sb.AppendLine("LastHeartbeatAgeMs: unknown");
            }
            sb.AppendLine($"MaxHeartbeatDelayMsSinceAttempt: {maxUiHeartbeatDelayMsSinceAttempt:F0}");
            sb.AppendLine($"LateHeartbeatCountSinceAttempt: {uiHeartbeatLateCountSinceAttempt}");
            return sb.ToString().TrimEnd();
        }

        private string BuildRuntimeResourceSnapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("运行时资源:");
            AppendPlaybackValue(sb, "AppMemory", () => Windows.System.MemoryManager.AppMemoryUsage.ToString());
            AppendPlaybackValue(sb, "AppMemoryLimit", () => Windows.System.MemoryManager.AppMemoryUsageLimit.ToString());
            AppendPlaybackValue(sb, "AppMemoryLevel", () => Windows.System.MemoryManager.AppMemoryUsageLevel.ToString());
            AppendPlaybackValue(sb, "ManagedMemory", () => GC.GetTotalMemory(false).ToString());
            AppendPlaybackValue(sb, "GC Gen0", () => GC.CollectionCount(0).ToString());
            AppendPlaybackValue(sb, "GC Gen1", () => GC.CollectionCount(1).ToString());
            AppendPlaybackValue(sb, "GC Gen2", () => GC.CollectionCount(2).ToString());
            try
            {
                int worker;
                int completion;
                int maxWorker;
                int maxCompletion;
                System.Threading.ThreadPool.GetAvailableThreads(out worker, out completion);
                System.Threading.ThreadPool.GetMaxThreads(out maxWorker, out maxCompletion);
                sb.AppendLine($"ThreadPoolAvailable: worker={worker}/{maxWorker} io={completion}/{maxCompletion}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ThreadPoolAvailable: <err {ex.GetType().Name} 0x{ex.HResult:X8}>");
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildNetworkStateSnapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("网络状态:");
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null)
                {
                    sb.AppendLine("Profile: null");
                    return sb.ToString().TrimEnd();
                }
                sb.AppendLine($"ProfileName: {profile.ProfileName}");
                sb.AppendLine($"ConnectivityLevel: {profile.GetNetworkConnectivityLevel()}");
                var cost = profile.GetConnectionCost();
                if (cost != null)
                {
                    sb.AppendLine($"NetworkCostType: {cost.NetworkCostType}");
                    sb.AppendLine($"Roaming: {cost.Roaming}");
                    sb.AppendLine($"OverDataLimit: {cost.OverDataLimit}");
                    sb.AppendLine($"ApproachingDataLimit: {cost.ApproachingDataLimit}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"网络状态读取失败: {BuildExceptionSummary(ex)}");
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
            LogDebugIfEnabled(() => $"[重连状态] {status.Source} inProgress={status.IsReconnecting} {status.Attempt}/{status.MaxAttempt} {status.Message}");
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
                        LogDebugIfEnabled("网络已断开");
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
                        LogDebugIfEnabled("网络已恢复，触发重连");
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
                if (IsDebugDiagnosticsEnabled())
                {
                    LogHelper.Log(JoinNonEmpty(
                        $"Buffering 超过 {BufferingTimeout.TotalSeconds:F0}s，触发流重连。State={state}",
                        BuildSafePlaybackContext(),
                        BuildRecentPlaybackEventsSnapshot(),
                        BuildRuntimeResourceSnapshot(),
                        BuildNetworkStateSnapshot()), LogType.DEBUG);
                }
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
                    LogDebugIfEnabled(() => $"忽略重复播放请求 source={source} urlHash={url.GetHashCode()}");
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
                    LogDebugIfEnabled(() => $"流重连调度 reason={reason} attempt={streamReconnectAttempt} level={level} delay={delay.TotalSeconds}s");

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
                case ExperimentalD3D11VideoDecoderIndex:
                    return "实验D3D11VA";
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
            if (!IsDebugDiagnosticsEnabled())
            {
                return null;
            }

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
                if (!IsDebugDiagnosticsEnabled())
                {
                    return;
                }

                var probe = await ProbeUrlAsync(url);
                var throughputProbe = await ProbeThroughputAsync(url);
                var flvProbe = await ProbeFlvAsync(url);
                var connectivityProbe = await ProbeConnectivityBaselineAsync();
                var correlation = BuildProbeCorrelationSummary(probe, throughputProbe, flvProbe, connectivityProbe);
                if (!IsDebugDiagnosticsEnabled())
                {
                    return;
                }
                var merged = JoinNonEmpty(
                    $"短播放预检: {reasonText}",
                    lastMediaInfoSnapshot,
                    BuildRecentPlaybackSamplesSnapshot(),
                    BuildRecentPlaybackEventsSnapshot(),
                    BuildUiHealthSnapshot(DateTimeOffset.UtcNow),
                    BuildRuntimeResourceSnapshot(),
                    BuildNetworkStateSnapshot(),
                    correlation,
                    probe,
                    throughputProbe,
                    flvProbe,
                    connectivityProbe);
                if (!string.IsNullOrEmpty(merged))
                {
                    LogHelper.Log(merged, LogType.DEBUG);
                }
            });
            return $"短播放预检触发: {reasonText}";
        }
        private async Task<string> ProbeFlvAsync(string url)
        {
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrEmpty(url))
            {
                return null;
            }
            var probeStarted = Stopwatch.StartNew();
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("FLV样本预检:");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                AppendProbeTargetSummary(sb, uri);
                var maxBytes = FlvDiagnosticProbeBytes;
                using (var client = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    ApplyProbeHeaders(request);
                    request.Headers.Append("Range", $"bytes=0-{maxBytes - 1}");
                    var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    sb.AppendLine($"GET Range=0-{maxBytes - 1} => {(int)response.StatusCode} {response.ReasonPhrase}");
                    AppendHttpStatusClassification(sb, response.StatusCode);
                    if (response.Content?.Headers?.ContentType != null)
                    {
                        sb.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
                    }
                    if (response.Content?.Headers?.ContentLength != null)
                    {
                        sb.AppendLine($"Content-Length: {response.Content.Headers.ContentLength}");
                    }
                    if (response.Content == null)
                    {
                        sb.AppendLine("响应无 Content");
                        probeStarted.Stop();
                        sb.AppendLine($"FLV样本预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                        return sb.ToString().TrimEnd();
                    }
                    using (var stream = await response.Content.ReadAsInputStreamAsync())
                    {
                        var read = await ReadProbeBytesAsync(stream, maxBytes, ProbeReadTimeout);
                        AppendProbeReadSummary(sb, "FLV样本读取", read);
                        if (read.BytesRead == 0 || read.Buffer == null)
                        {
                            sb.AppendLine("样本读取为空");
                        }
                        else
                        {
                            sb.AppendLine(AnalyzeFlvTags(read.Buffer, (int)read.BytesRead));
                        }
                    }
                    probeStarted.Stop();
                    sb.AppendLine($"FLV样本预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                probeStarted.Stop();
                return BuildProbeFailure("FLV样本预检失败", ex, probeStarted.ElapsedMilliseconds);
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
            var incompleteOffset = -1;
            long firstAudioTs = -1;
            long lastAudioTs = -1;
            long firstVideoTs = -1;
            long lastVideoTs = -1;
            long maxAudioGap = 0;
            long maxVideoGap = 0;
            var audioBackwards = 0;
            var videoBackwards = 0;
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
                    var timestamp = ReadFlvTimestamp(buffer, offset);
                    UpdateTimestampStats(timestamp, ref firstAudioTs, ref lastAudioTs, ref maxAudioGap, ref audioBackwards);
                }
                else if (tagType == 9)
                {
                    video++;
                    if (firstVideo < 0) firstVideo = offset;
                    var timestamp = ReadFlvTimestamp(buffer, offset);
                    UpdateTimestampStats(timestamp, ref firstVideoTs, ref lastVideoTs, ref maxVideoGap, ref videoBackwards);
                }
                else if (tagType == 18)
                {
                    script++;
                }
                tags++;
                var next = offset + 11 + dataSize + 4;
                if (next <= offset || next > length)
                {
                    incompleteOffset = offset;
                    break;
                }
                offset = next;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"FLV标签统计: tags={tags} audio={audio} video={video} script={script}");
            sb.AppendLine($"首音频偏移: {(firstAudio < 0 ? "无" : firstAudio.ToString())}");
            sb.AppendLine($"首视频偏移: {(firstVideo < 0 ? "无" : firstVideo.ToString())}");
            sb.AppendLine($"音频时间戳: first={FormatFlvTimestamp(firstAudioTs)} last={FormatFlvTimestamp(lastAudioTs)} span={FormatFlvTimestampSpan(firstAudioTs, lastAudioTs)} maxGap={maxAudioGap} backwards={audioBackwards}");
            sb.AppendLine($"视频时间戳: first={FormatFlvTimestamp(firstVideoTs)} last={FormatFlvTimestamp(lastVideoTs)} span={FormatFlvTimestampSpan(firstVideoTs, lastVideoTs)} maxGap={maxVideoGap} backwards={videoBackwards}");
            if (firstAudioTs >= 0 && firstVideoTs >= 0)
            {
                sb.AppendLine($"首帧音视频时间差 VideoMinusAudioMs={firstVideoTs - firstAudioTs}");
            }
            if (lastAudioTs >= 0 && lastVideoTs >= 0)
            {
                sb.AppendLine($"末帧音视频时间差 VideoMinusAudioMs={lastVideoTs - lastAudioTs}");
            }
            if (incompleteOffset >= 0)
            {
                sb.AppendLine($"样本末尾未完整标签偏移: {incompleteOffset}");
            }
            sb.AppendLine($"样本长度: {length}");
            return sb.ToString().TrimEnd();
        }
        private static long ReadFlvTimestamp(byte[] buffer, int offset)
        {
            if (buffer == null || offset + 7 >= buffer.Length)
            {
                return -1;
            }
            return ((long)buffer[offset + 7] << 24) |
                ((long)buffer[offset + 4] << 16) |
                ((long)buffer[offset + 5] << 8) |
                buffer[offset + 6];
        }
        private static void UpdateTimestampStats(long timestamp, ref long first, ref long last, ref long maxGap, ref int backwards)
        {
            if (timestamp < 0)
            {
                return;
            }
            if (first < 0)
            {
                first = timestamp;
            }
            if (last >= 0)
            {
                var gap = timestamp - last;
                if (gap < 0)
                {
                    backwards++;
                }
                else if (gap > maxGap)
                {
                    maxGap = gap;
                }
            }
            last = timestamp;
        }
        private static string FormatFlvTimestamp(long timestamp)
        {
            return timestamp < 0 ? "无" : timestamp.ToString();
        }
        private static string FormatFlvTimestampSpan(long first, long last)
        {
            return first < 0 || last < 0 ? "无" : (last - first).ToString();
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
        private string BuildProbeFailure(string title, Exception ex, long elapsedMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{title}: {BuildExceptionSummary(ex)}");
            var classification = BuildNetworkFailureClassification(ex);
            if (!string.IsNullOrEmpty(classification))
            {
                sb.AppendLine(classification);
            }
            if (elapsedMs >= 0)
            {
                sb.AppendLine($"ProbeElapsedMs: {elapsedMs}");
            }
            return sb.ToString().TrimEnd();
        }
        private string BuildNetworkFailureClassification(Exception ex)
        {
            if (ex == null)
            {
                return null;
            }
            var status = TryGetWebErrorStatus(ex);
            var hresult = GetPrimaryHResult(ex);
            var win32Code = TryGetWin32Code(hresult);
            var category = ClassifyNetworkFailure(status, hresult, ex);
            var sb = new StringBuilder();
            sb.AppendLine("网络错误归类:");
            sb.AppendLine($"NetworkFailureCategory: {category}");
            if (status.HasValue)
            {
                sb.AppendLine($"WebErrorStatus: {status.Value}");
            }
            sb.AppendLine($"HResult: 0x{hresult:X8}");
            if (win32Code.HasValue)
            {
                sb.AppendLine($"Win32Code: {win32Code.Value}");
            }
            sb.AppendLine($"LikelyLayer: {BuildLikelyNetworkLayer(category)}");
            return sb.ToString().TrimEnd();
        }
        private static WebErrorStatus? TryGetWebErrorStatus(Exception ex)
        {
            var current = ex;
            var depth = 0;
            while (current != null && depth < 5)
            {
                try
                {
                    var status = WebError.GetStatus(current.HResult);
                    if (status != WebErrorStatus.Unknown)
                    {
                        return status;
                    }
                }
                catch
                {
                }
                current = current.InnerException;
                depth++;
            }
            return null;
        }
        private static int GetPrimaryHResult(Exception ex)
        {
            var current = ex;
            var depth = 0;
            while (current != null && depth < 5)
            {
                if (current.HResult != 0)
                {
                    return current.HResult;
                }
                current = current.InnerException;
                depth++;
            }
            return 0;
        }
        private static int? TryGetWin32Code(int hresult)
        {
            var value = unchecked((uint)hresult);
            if ((value & 0xFFFF0000) == 0x80070000)
            {
                return (int)(value & 0xFFFF);
            }
            return null;
        }
        private static string ClassifyNetworkFailure(WebErrorStatus? status, int hresult, Exception ex)
        {
            var statusText = status.HasValue ? status.Value.ToString() : string.Empty;
            var message = ex?.Message ?? string.Empty;
            if (statusText == "HostNameNotResolved" ||
                hresult == unchecked((int)0x80072EE7) ||
                message.IndexOf("无法解析", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("resolve", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DnsNameResolutionFailure";
            }
            if (statusText == "CannotConnect" ||
                statusText == "ServerUnreachable" ||
                hresult == unchecked((int)0x80072EFD))
            {
                return "ConnectFailure";
            }
            if (statusText == "Timeout" ||
                statusText == "GatewayTimeout" ||
                hresult == unchecked((int)0x80072EE2))
            {
                return "NetworkTimeout";
            }
            if (statusText == "ConnectionAborted" ||
                statusText == "ConnectionReset" ||
                statusText == "Disconnected" ||
                hresult == unchecked((int)0x80072EFE))
            {
                return "ConnectionInterrupted";
            }
            if (statusText.IndexOf("Certificate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                statusText.IndexOf("Https", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "TlsOrCertificateFailure";
            }
            if (statusText == "Forbidden" ||
                statusText == "Unauthorized" ||
                statusText == "NotFound" ||
                statusText == "TooManyRequests" ||
                statusText == "BadGateway" ||
                statusText == "ServiceUnavailable")
            {
                return "HttpStatusFailure";
            }
            return "UnknownNetworkFailure";
        }
        private static string BuildLikelyNetworkLayer(string category)
        {
            switch (category)
            {
                case "DnsNameResolutionFailure":
                    return "DNS/NameResolution";
                case "ConnectFailure":
                    return "TCP/Route/CDNReachability";
                case "NetworkTimeout":
                    return "NetworkTimeout/CDNLatency";
                case "ConnectionInterrupted":
                    return "ConnectionResetOrDrop";
                case "TlsOrCertificateFailure":
                    return "TLS/Certificate";
                case "HttpStatusFailure":
                    return "HTTP/CDNPolicy";
                default:
                    return "Unknown";
            }
        }
        private static void AppendProbeTargetSummary(StringBuilder sb, Uri uri)
        {
            if (sb == null || uri == null)
            {
                return;
            }
            sb.AppendLine("ProbeTarget:");
            sb.AppendLine($"Scheme: {uri.Scheme}");
            sb.AppendLine($"Host: {uri.Host}");
            sb.AppendLine($"Port: {uri.Port}");
            sb.AppendLine($"PathExt: {Path.GetExtension(uri.AbsolutePath)}");
            sb.AppendLine($"PathLength: {uri.AbsolutePath?.Length ?? 0}");
            sb.AppendLine($"QueryLength: {uri.Query?.Length ?? 0}");
        }
        private static void AppendHttpStatusClassification(StringBuilder sb, HttpStatusCode statusCode)
        {
            if (sb == null)
            {
                return;
            }
            var code = (int)statusCode;
            string category;
            if (code >= 200 && code <= 299)
            {
                category = "Success";
            }
            else if (code >= 300 && code <= 399)
            {
                category = "Redirect";
            }
            else if (code == 401 || code == 403)
            {
                category = "AuthOrHotlinkDenied";
            }
            else if (code == 404 || code == 410)
            {
                category = "UrlMissingOrExpired";
            }
            else if (code == 416)
            {
                category = "RangeRejected";
            }
            else if (code == 429)
            {
                category = "RateLimited";
            }
            else if (code >= 500 && code <= 599)
            {
                category = "CdnServerError";
            }
            else
            {
                category = "OtherHttpStatus";
            }
            sb.AppendLine($"HttpStatusCategory: {category}");
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
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrEmpty(url))
            {
                return null;
            }
            var probeStarted = Stopwatch.StartNew();
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("URL预检:");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Scheme: {uri.Scheme}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                AppendProbeTargetSummary(sb, uri);
                var headResult = await SendProbeAsync(uri, "HEAD", useRange: false);
                sb.AppendLine(headResult);
                var rangeResult = await SendProbeAsync(uri, "GET", useRange: true);
                sb.AppendLine(rangeResult);
                probeStarted.Stop();
                sb.AppendLine($"URL预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                probeStarted.Stop();
                return BuildProbeFailure("URL预检失败", ex, probeStarted.ElapsedMilliseconds);
            }
        }
        private async Task<string> ProbeThroughputAsync(string url)
        {
            if (!IsDebugDiagnosticsEnabled() ||
                string.IsNullOrEmpty(url))
            {
                return null;
            }
            var probeStarted = Stopwatch.StartNew();
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine("吞吐预检:");
                sb.AppendLine($"Host: {uri.Host}");
                sb.AppendLine($"Scheme: {uri.Scheme}");
                sb.AppendLine($"Path: {uri.AbsolutePath}");
                AppendProbeTargetSummary(sb, uri);
                using (var client = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    ApplyProbeHeaders(request);
                    request.Headers.Append("Range", $"bytes=0-{ThroughputProbeBytes - 1}");
                    var responseStarted = Stopwatch.StartNew();
                    var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    responseStarted.Stop();
                    sb.AppendLine($"GET Range=0-{ThroughputProbeBytes - 1} => {(int)response.StatusCode} {response.ReasonPhrase} headerElapsedMs={responseStarted.ElapsedMilliseconds}");
                    AppendHttpStatusClassification(sb, response.StatusCode);
                    if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != uri)
                    {
                        sb.AppendLine("Final-Uri:");
                        sb.AppendLine(BuildUrlSummary(response.RequestMessage.RequestUri.ToString()));
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
                    if (response.Content == null)
                    {
                        sb.AppendLine("响应无 Content");
                        probeStarted.Stop();
                        sb.AppendLine($"吞吐预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                        return sb.ToString().TrimEnd();
                    }
                    using (var stream = await response.Content.ReadAsInputStreamAsync())
                    {
                        var read = await ReadProbeBytesAsync(stream, ThroughputProbeBytes, ProbeReadTimeout);
                        AppendProbeReadSummary(sb, "吞吐读取", read);
                    }
                    probeStarted.Stop();
                    sb.AppendLine($"吞吐预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                probeStarted.Stop();
                return BuildProbeFailure("吞吐预检失败", ex, probeStarted.ElapsedMilliseconds);
            }
        }
        private async Task<string> ProbeConnectivityBaselineAsync()
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return null;
            }

            var probeStarted = Stopwatch.StartNew();
            try
            {
                var uri = new Uri(ConnectivityProbeUrl);
                var sb = new StringBuilder();
                sb.AppendLine("网络对照预检:");
                sb.AppendLine("Purpose: 判断AppContainer内通用HTTP联网是否可用，不代表直播CDN健康");
                AppendProbeTargetSummary(sb, uri);
                using (var client = new HttpClient())
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    request.Headers.Append("Range", $"bytes=0-{ConnectivityProbeBytes - 1}");
                    var responseStarted = Stopwatch.StartNew();
                    var response = await client.SendRequestAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    responseStarted.Stop();
                    sb.AppendLine($"GET Range=0-{ConnectivityProbeBytes - 1} => {(int)response.StatusCode} {response.ReasonPhrase} headerElapsedMs={responseStarted.ElapsedMilliseconds}");
                    AppendHttpStatusClassification(sb, response.StatusCode);
                    if (response.Content?.Headers?.ContentType != null)
                    {
                        sb.AppendLine($"Content-Type: {response.Content.Headers.ContentType}");
                    }
                    if (response.Content?.Headers?.ContentLength != null)
                    {
                        sb.AppendLine($"Content-Length: {response.Content.Headers.ContentLength}");
                    }
                    if (response.Content == null)
                    {
                        sb.AppendLine("响应无 Content");
                        probeStarted.Stop();
                        sb.AppendLine($"网络对照预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                        return sb.ToString().TrimEnd();
                    }
                    using (var stream = await response.Content.ReadAsInputStreamAsync())
                    {
                        var read = await ReadProbeBytesAsync(stream, ConnectivityProbeBytes, ProbeReadTimeout);
                        AppendProbeReadSummary(sb, "对照读取", read);
                    }
                    probeStarted.Stop();
                    sb.AppendLine($"网络对照预检耗时Ms: {probeStarted.ElapsedMilliseconds}");
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                probeStarted.Stop();
                return BuildProbeFailure("网络对照预检失败", ex, probeStarted.ElapsedMilliseconds);
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
            var responseStarted = Stopwatch.StartNew();
            try
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
                    responseStarted.Stop();
                    var sb = new StringBuilder();
                    sb.AppendLine($"{method} {(useRange ? "Range=0-4095" : "")} => {(int)response.StatusCode} {response.ReasonPhrase} headerElapsedMs={responseStarted.ElapsedMilliseconds}");
                    AppendHttpStatusClassification(sb, response.StatusCode);
                    if (response.RequestMessage?.RequestUri != null && response.RequestMessage.RequestUri != uri)
                    {
                        sb.AppendLine("Final-Uri:");
                        sb.AppendLine(BuildUrlSummary(response.RequestMessage.RequestUri.ToString()));
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
                            {
                                var read = await ReadProbeBytesAsync(stream, UrlFirstBytesProbeBytes, ProbeReadTimeout);
                                AppendProbeReadSummary(sb, "首包读取", read);
                                if (read.BytesRead > 0 && read.Buffer != null)
                                {
                                    sb.AppendLine(AnalyzeFirstBytes(read.Buffer));
                                }
                                else
                                {
                                    sb.AppendLine("首包读取为空");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine(BuildProbeFailure("首包读取失败", ex, -1));
                        }
                    }
                    return sb.ToString().TrimEnd();
                }
            }
            catch (Exception ex)
            {
                responseStarted.Stop();
                return BuildProbeFailure($"{method} {(useRange ? "Range=0-4095 " : "")}请求失败", ex, responseStarted.ElapsedMilliseconds);
            }
        }
        private async Task<ProbeReadResult> ReadProbeBytesAsync(IInputStream stream, uint maxBytes, TimeSpan timeout)
        {
            var result = new ProbeReadResult
            {
                Buffer = new byte[0]
            };
            if (stream == null || maxBytes == 0)
            {
                return result;
            }

            var max = maxBytes > int.MaxValue ? int.MaxValue : (int)maxBytes;
            var bytes = new List<byte>(Math.Min(max, 65536));
            var sw = Stopwatch.StartNew();
            using (var cts = new System.Threading.CancellationTokenSource(timeout))
            using (var reader = new DataReader(stream))
            {
                reader.InputStreamOptions = InputStreamOptions.Partial;
                try
                {
                    while (bytes.Count < max)
                    {
                        var remaining = max - bytes.Count;
                        var requestSize = (uint)Math.Min(32768, remaining);
                        var loaded = await reader.LoadAsync(requestSize).AsTask(cts.Token);
                        if (loaded == 0)
                        {
                            break;
                        }
                        if (result.FirstByteMs < 0)
                        {
                            result.FirstByteMs = sw.ElapsedMilliseconds;
                        }
                        var chunk = new byte[(int)loaded];
                        reader.ReadBytes(chunk);
                        bytes.AddRange(chunk);
                    }
                }
                catch (TaskCanceledException)
                {
                    result.TimedOut = true;
                }
                catch (OperationCanceledException)
                {
                    result.TimedOut = true;
                }
                catch (Exception ex)
                {
                    result.Error = BuildProbeFailure("读取异常", ex, -1);
                }
            }
            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.BytesRead = (uint)bytes.Count;
            result.Buffer = bytes.ToArray();
            return result;
        }

        private void AppendProbeReadSummary(StringBuilder sb, string title, ProbeReadResult read)
        {
            if (sb == null || read == null)
            {
                return;
            }
            sb.AppendLine($"{title}: bytes={read.BytesRead} firstByteMs={read.FirstByteMs} elapsedMs={read.ElapsedMs} timedOut={read.TimedOut}");
            if (read.BytesRead > 0 && read.ElapsedMs > 0)
            {
                var kbps = read.BytesRead * 1000.0 / read.ElapsedMs / 1024.0;
                sb.AppendLine($"{title}平均速度KBps={kbps:F1}");
            }
            if (!string.IsNullOrEmpty(read.Error))
            {
                sb.AppendLine($"{title}错误: {read.Error}");
            }
            if (read.TimedOut)
            {
                sb.AppendLine($"{title}结果分类: ReadTimeout");
            }
            else if (read.BytesRead == 0)
            {
                sb.AppendLine($"{title}结果分类: NoData");
            }
            else if (read.ElapsedMs > 0)
            {
                var kbps = read.BytesRead * 1000.0 / read.ElapsedMs / 1024.0;
                sb.AppendLine($"{title}结果分类: {(kbps < 128 ? "VerySlowRead" : "Readable")}");
            }
        }
        private string BuildProbeCorrelationSummary(string urlProbe, string throughputProbe, string flvProbe, string connectivityProbe)
        {
            if (!IsDebugDiagnosticsEnabled())
            {
                return null;
            }

            var targetText = JoinNonEmpty(urlProbe, throughputProbe, flvProbe) ?? string.Empty;
            var baselineText = connectivityProbe ?? string.Empty;
            var targetDnsFailure = ContainsOrdinalIgnoreCase(targetText, "NetworkFailureCategory: DnsNameResolutionFailure");
            var targetConnectFailure = ContainsOrdinalIgnoreCase(targetText, "NetworkFailureCategory: ConnectFailure");
            var targetTimeout = ContainsOrdinalIgnoreCase(targetText, "NetworkFailureCategory: NetworkTimeout") ||
                ContainsOrdinalIgnoreCase(targetText, "结果分类: ReadTimeout");
            var targetSlowRead = ContainsOrdinalIgnoreCase(targetText, "结果分类: VerySlowRead");
            var targetHttpFailure = ContainsOrdinalIgnoreCase(targetText, "HttpStatusCategory: AuthOrHotlinkDenied") ||
                ContainsOrdinalIgnoreCase(targetText, "HttpStatusCategory: UrlMissingOrExpired") ||
                ContainsOrdinalIgnoreCase(targetText, "HttpStatusCategory: RateLimited") ||
                ContainsOrdinalIgnoreCase(targetText, "HttpStatusCategory: CdnServerError");
            var baselineFailure = ContainsOrdinalIgnoreCase(baselineText, "网络对照预检失败") ||
                ContainsOrdinalIgnoreCase(baselineText, "NetworkFailureCategory:") ||
                ContainsOrdinalIgnoreCase(baselineText, "结果分类: ReadTimeout") ||
                ContainsOrdinalIgnoreCase(baselineText, "结果分类: NoData");
            var baselineSuccess = ContainsOrdinalIgnoreCase(baselineText, "HttpStatusCategory: Success") &&
                ContainsOrdinalIgnoreCase(baselineText, "对照读取结果分类: Readable");

            var sb = new StringBuilder();
            sb.AppendLine("网络探测归因:");
            sb.AppendLine($"TargetDnsFailure: {targetDnsFailure}");
            sb.AppendLine($"TargetConnectFailure: {targetConnectFailure}");
            sb.AppendLine($"TargetTimeout: {targetTimeout}");
            sb.AppendLine($"TargetSlowRead: {targetSlowRead}");
            sb.AppendLine($"TargetHttpFailure: {targetHttpFailure}");
            sb.AppendLine($"BaselineSuccess: {baselineSuccess}");
            sb.AppendLine($"BaselineFailure: {baselineFailure}");
            if (targetDnsFailure && baselineSuccess)
            {
                sb.AppendLine("LikelyRoot: CurrentCdnDnsOrHostResolutionFailure");
            }
            else if ((targetDnsFailure || targetConnectFailure || targetTimeout) && baselineFailure)
            {
                sb.AppendLine("LikelyRoot: AppContainerOrSystemNetworkFailure");
            }
            else if (targetConnectFailure && baselineSuccess)
            {
                sb.AppendLine("LikelyRoot: CurrentCdnConnectionFailure");
            }
            else if ((targetSlowRead || targetTimeout) && baselineSuccess)
            {
                sb.AppendLine("LikelyRoot: CurrentCdnThroughputOrReadStall");
            }
            else if (targetHttpFailure)
            {
                sb.AppendLine("LikelyRoot: CurrentCdnHttpStatusFailure");
            }
            else
            {
                sb.AppendLine("LikelyRoot: UnclassifiedProbeResult");
            }
            return sb.ToString().TrimEnd();
        }
        private static bool ContainsOrdinalIgnoreCase(string text, string value)
        {
            return !string.IsNullOrEmpty(text) &&
                !string.IsNullOrEmpty(value) &&
                text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            var eventsSnapshot = BuildRecentPlaybackEventsSnapshot();
            if (!string.IsNullOrEmpty(eventsSnapshot))
            {
                sb.AppendLine(eventsSnapshot);
            }
            var uiHealth = BuildUiHealthSnapshot(DateTimeOffset.UtcNow);
            if (!string.IsNullOrEmpty(uiHealth))
            {
                sb.AppendLine(uiHealth);
            }
            var resource = BuildRuntimeResourceSnapshot();
            if (!string.IsNullOrEmpty(resource))
            {
                sb.AppendLine(resource);
            }
            var network = BuildNetworkStateSnapshot();
            if (!string.IsNullOrEmpty(network))
            {
                sb.AppendLine(network);
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

        private void StartMediaSourceFailureDiagnostics(string title, Exception ex, string url, string playbackContext, string configSnapshot, string urlAnalysis, string failureSummary)
        {
            if (!IsDebugDiagnosticsEnabled() || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _ = LogMediaSourceFailureDiagnosticsAsync(title, ex, url, playbackContext, configSnapshot, urlAnalysis, failureSummary);
        }

        private async Task LogMediaSourceFailureDiagnosticsAsync(string title, Exception ex, string url, string playbackContext, string configSnapshot, string urlAnalysis, string failureSummary)
        {
            try
            {
                var probe = await ProbeUrlAsync(url);
                var connectivityProbe = await ProbeConnectivityBaselineAsync();
                var correlation = BuildProbeCorrelationSummary(probe, null, null, connectivityProbe);
                if (!IsDebugDiagnosticsEnabled())
                {
                    return;
                }

                var merged = JoinNonEmpty(
                    $"{title}后台网络诊断（不阻塞切换线路）",
                    playbackContext,
                    configSnapshot,
                    urlAnalysis,
                    failureSummary,
                    BuildExceptionDetail(ex),
                    correlation,
                    probe,
                    connectivityProbe);
                if (!string.IsNullOrEmpty(merged))
                {
                    LogHelper.Log(merged, LogType.DEBUG);
                }
            }
            catch (Exception diagnosticEx)
            {
                LogDebugIfEnabled(() => $"播放器失败后台诊断异常: {diagnosticEx.GetType().FullName} 0x{diagnosticEx.HResult:X8} {diagnosticEx.Message}");
            }
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
                AddPlaybackEventHistory($"BeginSetPlayer urlHash={url?.GetHashCode()}", mediaPlayer?.PlaybackSession);
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
                    case ExperimentalD3D11VideoDecoderIndex:
                        config.Video.VideoDecoderMode = VideoDecoderMode.Automatic;
                        config.FFmpegOptions.Add("hwaccel", "d3d11va");
                        config.FFmpegOptions.Add("hwaccel_output_format", "d3d11");
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
                lastConfigSnapshot = IsDebugDiagnosticsEnabled() ? BuildConfigSnapshot(config) : null;
                lastUrlAnalysis = IsDebugDiagnosticsEnabled() ? BuildUrlAnalysis(url) : null;
                lastProbeSnapshot = null;
                if (IsDebugDiagnosticsEnabled())
                {
                    await EnsureDiagnosticsSnapshotAsync();
                }
                if (!IsMediaSourceAttemptCurrent(attemptVersion))
                {
                    return;
                }
                if (IsDebugDiagnosticsEnabled())
                {
                    var attemptLog = JoinNonEmpty("播放尝试", BuildPlaybackContext(), lastConfigSnapshot, lastUrlAnalysis);
                    if (!string.IsNullOrEmpty(attemptLog))
                    {
                        LogHelper.Log(attemptLog, LogType.DEBUG);
                    }
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
                            var timeoutException = new TimeoutException($"FFmpegMediaSource.CreateFromUriAsync 超时 {createTimeout.Value.TotalSeconds:F0}s");
                            var playbackContext = IsDebugDiagnosticsEnabled() ? BuildPlaybackContext() : null;
                            var timeoutSummary = $"播放器建源超时: {createTimeout.Value.TotalSeconds:F0}s elapsedMs={(DateTimeOffset.UtcNow - createStartedUtc).TotalMilliseconds:F0}";
                            if (IsDebugDiagnosticsEnabled())
                            {
                                LogPlayError("播放器初始化超时，立即切换下一线路", timeoutException,
                                    JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, timeoutSummary, "详细网络诊断已转入后台"));
                                StartMediaSourceFailureDiagnostics("播放器初始化超时", timeoutException, url,
                                    playbackContext, lastConfigSnapshot, lastUrlAnalysis, timeoutSummary);
                            }
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
                        LogDebugIfEnabled(() => $"播放器建源完成 elapsedMs={(DateTimeOffset.UtcNow - createStartedUtc).TotalMilliseconds:F0} 线路: {liveRoomVM?.CurrentLine?.Name}");
                    }
                }
                catch (Exception ex)
                {
                    if (!IsMediaSourceAttemptCurrent(attemptVersion))
                    {
                        return;
                    }
                    if (IsDebugDiagnosticsEnabled())
                    {
                        var playbackContext = BuildPlaybackContext();
                        LogPlayError("播放器初始化失败，立即切换下一线路", ex,
                            JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, "详细网络诊断已转入后台"));
                        StartMediaSourceFailureDiagnostics("播放器初始化失败", ex, url,
                            playbackContext, lastConfigSnapshot, lastUrlAnalysis, null);
                    }
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
                string mergedExtra = null;
                if (IsDebugDiagnosticsEnabled())
                {
                    var probe = await ProbeUrlAsync(url);
                    var connectivityProbe = await ProbeConnectivityBaselineAsync();
                    var correlation = BuildProbeCorrelationSummary(probe, null, null, connectivityProbe);
                    lastProbeSnapshot = probe;
                    mergedExtra = JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, correlation, probe, connectivityProbe);
                }
                if (IsDebugDiagnosticsEnabled())
                {
                    LogPlayError("播放失败", ex, mergedExtra);
                }
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
                    LogDebugIfEnabled(() => $"关闭后延迟建源已释放 urlHash={url?.GetHashCode()}");
                }
                else if (!IsMediaSourceAttemptCurrent(attemptVersion))
                {
                    LogDebugIfEnabled(() => $"过期建源已释放 urlHash={url?.GetHashCode()}");
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
                if (IsDebugDiagnosticsEnabled())
                {
                    LogPlayError("直播加载失败", null, JoinNonEmpty(lastConfigSnapshot, lastUrlAnalysis, lastProbeSnapshot));
                }
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
            LogDebugIfEnabled(() => $"虎牙播放异常，尝试刷新播放地址。原因: {reason}");
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

            LogDebugIfEnabled(() => $"哔哩哔哩直播播放异常，自动从 {currentCodec} 切换到 AVC。原因: {reason}");
            liveRoomVM.SelectedCodec = "AVC";
            return true;
        }

        private TimeSpan? GetMediaSourceCreateTimeout(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (liveRoomVM?.SiteName != "哔哩哔哩直播")
            {
                return null;
            }

            if (url.IndexOf("127.0.0.1:8789/api/bilibili/live.flv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BilibiliLocalProxyCreateSourceTimeout;
            }

            if (url.IndexOf(".flv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return BilibiliDirectFlvCreateSourceTimeout;
            }

            if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
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
            LogDebugIfEnabled(() => $"哔哩哔哩直播播放异常，重试当前线路。原因: {reason} 次数: {currentLineRetryCount}/2 线路: {liveRoomVM?.CurrentLine?.Name}");
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
                // 首次建源从未进入 Playing，说明当前 CDN/编码不能及时完成初始化。
                // 重试同一个签名 URL 通常只会再次等满建源超时；直接切换下一线路。
                LogDebugIfEnabled(() => $"哔哩哔哩直播首次建源未进入Playing，跳过同线路重试。线路: {liveRoomVM?.CurrentLine?.Name}");
                return false;
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
            playbackDiagnosticProbeCount = 0;
            currentBufferingStartedUtc = null;
            lastUiHeartbeatUtc = null;
            maxUiHeartbeatDelayMsSinceAttempt = 0;
            uiHeartbeatLateCountSinceAttempt = 0;
            lock (playbackDiagnosticsLock)
            {
                playbackSampleHistory.Clear();
                playbackEventHistory.Clear();
            }

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
            UpdateUiHeartbeat("ControlTimer");
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
            if (!IsDebugDiagnosticsEnabled())
            {
                return;
            }

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
            return value >= 0 && value <= ExperimentalD3D11VideoDecoderIndex ? value : DefaultVideoDecoderIndex;
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

            LogDebugIfEnabled(() => $"直播间解码器切换 roomId={liveRoomVM?.RoomID} decoder={GetDecoderLogText(decoder)}，重载当前线路");
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
                case ExperimentalD3D11VideoDecoderIndex:
                    return "D3D11VA";
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
                case ExperimentalD3D11VideoDecoderIndex:
                    return "ExperimentalD3D11VA";
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
