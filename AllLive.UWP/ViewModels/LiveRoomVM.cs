using AllLive.Core;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Windows.UI.Core;
using AllLive.UWP.Helper;
using System.Windows.Input;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.ApplicationModel.Core;
using System.ComponentModel;
using System.Timers;
using Windows.Foundation;
using System.Reflection;

namespace AllLive.UWP.ViewModels
{
    public class LiveRoomVM : BaseViewModel
    {
        SettingVM settingVM;
        public event EventHandler<string> ChangedPlayUrl;
        public event EventHandler<LiveMessage> AddDanmaku;
        public event EventHandler<ReconnectStatus> ReconnectStatusChanged;

        private bool isActive;
        private System.Threading.CancellationTokenSource danmakuReconnectCts;
        private int danmakuReconnectAttempt;
        private int reconnectInProgress;
        private object danmakuArgs;
        private static readonly TimeSpan[] DanmakuReconnectDelays = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
        };
        private const int MaxDanmakuReconnectAttempts = 6;
        public LiveRoomVM(SettingVM settingVM)
        {
            this.settingVM = settingVM;
            Messages = new ObservableCollection<LiveMessage>();
            SuperChatMessages = new ObservableCollection<SuperChatItem>();
            MessageCleanCount = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, 200);
            KeepSC = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true);
            AddFavoriteCommand = new RelayCommand(AddFavorite);
            RemoveFavoriteCommand = new RelayCommand(RemoveFavorite);

        }
        public ICommand AddFavoriteCommand { get; set; }
        public ICommand RemoveFavoriteCommand { get; set; }
        public int MessageCleanCount { get; set; } = 200;

        /// <summary>
        /// 保留SC
        /// </summary>
        private bool _keepSC;

        public bool KeepSC
        {
            get { return _keepSC; }
            set { _keepSC = value; DoPropertyChanged("KeepSC"); }
        }



        ILiveSite Site;
        ILiveDanmaku LiveDanmaku;

        private string _siteLogo = "ms-appx:///Assets/Placeholder/Placeholder1x1.png";

        public string SiteLogo
        {
            get { return _siteLogo; }
            set { _siteLogo = value; DoPropertyChanged("SiteLogo"); }
        }

        private string _siteName;
        public string SiteName
        {
            get { return _siteName; }
            set { _siteName = value; DoPropertyChanged("SiteName"); DoPropertyChanged(nameof(AudienceMetricToolTip)); }
        }

        private bool _isFavorite = false;
        public bool IsFavorite
        {
            get { return _isFavorite; }
            set { _isFavorite = value; DoPropertyChanged("IsFavorite"); }
        }

        private long? FavoriteID { get; set; }

        object RoomId;
        public LiveRoomDetail detail { get; set; }
        private bool allowPopularityFallback = true;

        private long _Online = 0;
        public long Online
        {
            get { return _Online; }
            set { _Online = value; DoPropertyChanged("Online"); }
        }

        private long? _viewerCount;
        public long? ViewerCount
        {
            get { return _viewerCount; }
            private set
            {
                if (_viewerCount == value)
                {
                    return;
                }
                _viewerCount = value;
                DoPropertyChanged(nameof(ViewerCount));
                RefreshAudienceDisplay();
            }
        }

        private long? _popularity;
        public long? Popularity
        {
            get { return _popularity; }
            private set
            {
                if (_popularity == value)
                {
                    return;
                }
                _popularity = value;
                DoPropertyChanged(nameof(Popularity));
                RefreshAudienceDisplay();
            }
        }

        private long? _vipCount;
        public long? VipCount
        {
            get { return _vipCount; }
            private set
            {
                if (_vipCount == value)
                {
                    return;
                }
                _vipCount = value;
                DoPropertyChanged(nameof(VipCount));
                RefreshAudienceDisplay();
            }
        }

        public string AudienceMetricToolTip
        {
            get
            {
                if (ViewerCount.HasValue)
                {
                    return "当前显示: 直播间在线人数";
                }
                if (VipCount.HasValue)
                {
                    return "当前显示: 贵宾数；真实在线人数接口请求失败或无返回";
                }
                if (Popularity.HasValue)
                {
                    if ((SiteName == "抖音直播" || SiteName == "哔哩哔哩直播") && Living)
                    {
                        return "当前显示: 人气/热度，弹幕连接成功后会自动切换为直播间观众数";
                    }
                    return "当前显示: 人气/热度；该平台暂未接入或尚未确认真实在线人数接口";
                }
                return "当前显示: 未知";
            }
        }

        private void RefreshAudienceDisplay()
        {
            var display = ViewerCount ?? VipCount ?? Popularity ?? 0;
            if (_Online != display)
            {
                _Online = display;
                DoPropertyChanged(nameof(Online));
            }
            DoPropertyChanged(nameof(AudienceMetricToolTip));
        }

        private void ApplyAudienceMetrics(LiveRoomDetail roomDetail)
        {
            if (roomDetail == null)
            {
                ViewerCount = null;
                VipCount = null;
                Popularity = null;
                allowPopularityFallback = true;
                return;
            }

            allowPopularityFallback = roomDetail.AllowPopularityFallback;
            ViewerCount = roomDetail.ViewerCount;
            VipCount = roomDetail.VipCount;
            Popularity = ShouldSuppressPopularityMetric() ? null : roomDetail.Popularity;
            if (!ViewerCount.HasValue && !VipCount.HasValue && !Popularity.HasValue && roomDetail.Online > 0 && allowPopularityFallback)
            {
                Popularity = roomDetail.Online;
            }
        }

        private bool IsBilibiliLiveRoom()
        {
            return string.Equals(Site?.Name ?? SiteName, "哔哩哔哩直播", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldSuppressPopularityMetric()
        {
            if (!IsBilibiliLiveRoom())
            {
                return false;
            }

            return ViewerCount.HasValue || !allowPopularityFallback;
        }

        private void ApplyAudienceMetricUpdate(LiveMessage message)
        {
            if (message == null)
            {
                return;
            }

            var value = Convert.ToInt64(message.Data);
            switch (message.AudienceMetricKind)
            {
                case LiveAudienceMetricKind.ViewerCount:
                    ViewerCount = value;
                    if (detail != null)
                    {
                        detail.ViewerCount = value;
                    }
                    return;
                case LiveAudienceMetricKind.VipCount:
                    VipCount = value;
                    if (detail != null)
                    {
                        detail.VipCount = value;
                    }
                    return;
                case LiveAudienceMetricKind.Popularity:
                    if (ShouldSuppressPopularityMetric())
                    {
                        return;
                    }
                    Popularity = value;
                    if (detail != null)
                    {
                        detail.Popularity = value;
                    }
                    return;
            }

            switch (SiteName)
            {
                case "抖音直播":
                    ViewerCount = value;
                    if (detail != null)
                    {
                        detail.ViewerCount = value;
                    }
                    break;
                default:
                    if (ShouldSuppressPopularityMetric())
                    {
                        return;
                    }
                    Popularity = value;
                    if (detail != null)
                    {
                        detail.Popularity = value;
                    }
                    break;
            }
        }
        private string _RoomID;

        public string RoomID
        {
            get { return _RoomID; }
            set { _RoomID = value; DoPropertyChanged("RoomID"); }
        }


        private string _photo = "ms-appx:///Assets/Placeholder/Placeholder1x1.png";
        public string Photo
        {
            get { return _photo; }
            set { _photo = value; DoPropertyChanged("Photo"); }
        }

        private string _name;
        public string Name
        {
            get { return _name; }
            set { _name = value; DoPropertyChanged("Name"); }
        }
        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; DoPropertyChanged("Title"); }
        }
        private bool _living = true;

        public bool Living
        {
            get { return _living; }
            set { _living = value; DoPropertyChanged("Living"); DoPropertyChanged(nameof(AudienceMetricToolTip)); }
        }


        private List<LivePlayQuality> qualities;
        public List<LivePlayQuality> Qualities
        {
            get { return qualities; }
            set { qualities = value; DoPropertyChanged("Qualities"); }
        }

        private LivePlayQuality currentQuality;
        public LivePlayQuality CurrentQuality
        {
            get { return currentQuality; }
            set
            {
                if (value == currentQuality)
                {
                    return;
                }
                currentQuality = value;
                DoPropertyChanged("CurrentQuality");
                if (value != null)
                {
                    LoadPlayUrl();
                }
            }

        }

        private List<PlayurlLine> lines;
        private List<PlayurlLine> allLines;
        public List<PlayurlLine> Lines
        {
            get { return lines; }
            set { lines = value; DoPropertyChanged("Lines"); }
        }


        private PlayurlLine currentLine;
        public PlayurlLine CurrentLine
        {
            get { return currentLine; }
            set
            {
                if (value == currentLine)
                {
                    return;
                }
                currentLine = value;
                DoPropertyChanged("CurrentLine");
                if (value != null)
                {
                    ChangedPlayUrl?.Invoke(this, value.Url);
                }
            }

        }

        private List<string> availableCodecs = new List<string>() { "全部" };
        public List<string> AvailableCodecs
        {
            get { return availableCodecs; }
            set { availableCodecs = value; DoPropertyChanged("AvailableCodecs"); }
        }

        private string selectedCodec = "全部";
        public string SelectedCodec
        {
            get { return selectedCodec; }
            set
            {
                if (value == selectedCodec)
                {
                    return;
                }
                selectedCodec = value;
                DoPropertyChanged("SelectedCodec");
                ApplyCodecFilter();
            }
        }

        public ObservableCollection<LiveMessage> Messages { get; set; }
        public ObservableCollection<SuperChatItem> SuperChatMessages { get; set; }

        public List<SettingsItem<double>> DanmakuOpacityItems { get; } = new List<SettingsItem<double>>()
        {
            new SettingsItem<double>(){ Name="100%",Value=1},
            new SettingsItem<double>(){ Name="90%",Value=0.9},
            new SettingsItem<double>(){ Name="80%",Value=0.8},
            new SettingsItem<double>(){ Name="70%",Value=0.7},
            new SettingsItem<double>(){ Name="60%",Value=0.6},
            new SettingsItem<double>(){ Name="50%",Value=0.5},
            new SettingsItem<double>(){ Name="40%",Value=0.4},
            new SettingsItem<double>(){ Name="30%",Value=0.3},
            new SettingsItem<double>(){ Name="20%",Value=0.2},
            new SettingsItem<double>(){ Name="10%",Value=0.1},
        };
        public List<SettingsItem<double>> DanmakuDiaplayAreaItems { get; } = new List<SettingsItem<double>>()
        {
            new SettingsItem<double>(){ Name="100%",Value=1},
            new SettingsItem<double>(){ Name="75%",Value=0.75},
            new SettingsItem<double>(){ Name="50%",Value=0.5},
            new SettingsItem<double>(){ Name="25%",Value=0.25},
        };
        public List<SettingsItem<int>> DanmakuSpeedItems { get; } = new List<SettingsItem<int>>()
        {
            new SettingsItem<int>(){ Name="极快",Value=2},
            new SettingsItem<int>(){ Name="很快",Value=4},
            new SettingsItem<int>(){ Name="较快",Value=6},
            new SettingsItem<int>(){ Name="快",Value=8},
            new SettingsItem<int>(){ Name="正常",Value=10},
            new SettingsItem<int>(){ Name="慢",Value=12},
            new SettingsItem<int>(){ Name="较慢",Value=14},
            new SettingsItem<int>(){ Name="很慢",Value=16},
            new SettingsItem<int>(){ Name="极慢",Value=18},
        };
        public List<SettingsItem<double>> DnamakuFontZoomItems { get; } = new List<SettingsItem<double>>()
        {
            new SettingsItem<double>(){ Name="极小",Value=0.2},
            new SettingsItem<double>(){ Name="很小",Value=0.6},
            new SettingsItem<double>(){ Name="较小",Value=0.8},
            new SettingsItem<double>(){ Name="小",Value=0.9},
            new SettingsItem<double>(){ Name="正常",Value=1.0},
            new SettingsItem<double>(){ Name="大",Value=1.1},
            new SettingsItem<double>(){ Name="较大",Value=1.2},
            new SettingsItem<double>(){ Name="很大",Value=1.4},
            new SettingsItem<double>(){ Name="极大",Value=1.8},
            new SettingsItem<double>(){ Name="特大",Value=2.0},
        };


        public async void LoadData(ILiveSite site, object roomId)
        {
            try
            {
                Loading = true;
                isActive = true;
                danmakuReconnectAttempt = 0;
                System.Threading.Interlocked.Exchange(ref reconnectInProgress, 0);
                CancelDanmakuReconnect();
                Site = site;

                RoomId = roomId;
                var sourceRoomId = roomId?.ToString();
                var result = await Site.GetRoomDetail(roomId);
                if (!isActive)
                {
                    return;
                }
                detail = result;
                LogRoomDetail(result);
                RoomID = result.RoomID;

                ApplyAudienceMetrics(result);
                Title = result.Title;
                SetWindowTitle();

                Name = result.UserName;
                MessageCenter.ChangeTitle(Title + " - " + Name, Site);
                if (!string.IsNullOrEmpty(result.UserAvatar))
                {
                    Photo = result.UserAvatar;
                }
                Living = result.Status;
                //加载SC
                LoadSuperChat();
                //检查收藏情况
                FavoriteID = DatabaseHelper.CheckFavorite(RoomID, Site.Name);
                if (FavoriteID == null
                    && !string.IsNullOrWhiteSpace(sourceRoomId)
                    && !string.Equals(RoomID, sourceRoomId, StringComparison.OrdinalIgnoreCase))
                {
                    FavoriteID = DatabaseHelper.CheckFavorite(sourceRoomId, Site.Name);
                }
                IsFavorite = FavoriteID != null;
                if (IsFavorite)
                {
                    MessageCenter.UpdateFavoriteLiveInfo(Site.Name, RoomID, sourceRoomId, Title, Living);
                }

                if (!isActive)
                {
                    return;
                }
                LiveDanmaku = Site.GetDanmaku();
                Messages.Add(new LiveMessage()
                {
                    Type = LiveMessageType.Chat,
                    UserName = "系统",
                    Message = "开始接收弹幕"
                });

                LiveDanmaku.NewMessage += LiveDanmaku_NewMessage;
                LiveDanmaku.OnClose += LiveDanmaku_OnClose;
                danmakuArgs = result.DanmakuData;
                try
                {
                    await LiveDanmaku.Start(result.DanmakuData);
                }
                catch (Exception ex)
                {
                    LogHelper.Log($"弹幕启动失败，继续加载播放。站点:{Site?.Name} 房间ID:{RoomID}", LogType.ERROR, ex);
                    Messages.Add(new LiveMessage()
                    {
                        Type = LiveMessageType.Chat,
                        UserName = "系统",
                        Message = "弹幕启动失败，已继续加载播放"
                    });
                    StartDanmakuReconnect(useInitialFailureMessage: true);
                }
                if (!isActive)
                {
                    return;
                }
                if (detail.Status)
                {
                    var qualities = await Site.GetPlayQuality(result);
                    if (!isActive)
                    {
                        return;
                    }
                    if (Site.Name == "虎牙直播")
                    {
                        //HDR无法播放
                        qualities = qualities.Where(x => !x.Quality.Contains("HDR")).ToList();
                    }
                    if (qualities == null || qualities.Count == 0)
                    {
                        LogHelper.Log($"获取清晰度失败: 返回空列表。站点:{Site?.Name} 房间ID:{RoomID}", LogType.ERROR);
                        if (Site?.Name == "斗鱼直播")
                        {
                            Utils.ShowMessageToast("斗鱼签名服务不可用，无法获取播放地址");
                        }
                        else
                        {
                            Utils.ShowMessageToast("加载清晰度失败");
                        }
                        return;
                    }
                    Qualities = qualities;
                    if (Qualities != null && Qualities.Count > 0)
                    {
                        CurrentQuality = Qualities[0];
                    }
                    // var u = await Site.GetPlayUrls(result, q[0]);
                    //ChangedPlayUrl?.Invoke(this, u[0]);
                }
                DatabaseHelper.AddHistory(new Models.HistoryItem()
                {
                    Photo = Photo,
                    RoomID = RoomID,
                    SiteName = Site.Name,
                    UserName = Name
                });

            }
            catch (DouyinRoomDataBlockedException ex)
            {
                LogHelper.Log(ex.Message, LogType.ERROR, ex);
                Utils.ShowMessageToast(DouyinRoomDataBlockedException.UserMessage);
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
            finally
            {
                Loading = false;
            }
        }

        Timer scTimer;
        public void SetSCTimer()
        {
            if (!isActive || Dispatcher == null)
            {
                return;
            }
            KeepSC = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true);
            if (KeepSC)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                 {
                     if (!isActive)
                     {
                         return;
                     }
                     foreach (var item in SuperChatMessages)
                     {
                         item.ShowCountdown = false;
                     }
                 });
                StopSCTimer();
            }
            else
            {
                StopSCTimer();
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    foreach (var item in SuperChatMessages)
                    {
                        item.ShowCountdown = true;
                        item.CountdownTime = Convert.ToInt32(item.EndTime.Subtract(DateTime.Now).TotalSeconds);
                    }
                });
                scTimer = new Timer(1000);
                scTimer.Elapsed += (s, e) =>
                {
                    if (!isActive || Dispatcher == null)
                    {
                        return;
                    }
                    _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        if (!isActive)
                        {
                            return;
                        }
                        for (var i = 0; i < SuperChatMessages.Count; i++)
                        {
                            var item = SuperChatMessages[i];
                            item.CountdownTime--;
                            if (item.CountdownTime <= 0)
                            {
                                SuperChatMessages.RemoveAt(i);
                            }
                        }
                    });

                };
                scTimer.Start();
            }
        }

        private void StopSCTimer()
        {
            scTimer?.Stop();
            scTimer?.Dispose();
            scTimer = null;
        }

        private void SetWindowTitle()
        {
            if (SettingHelper.GetValue(SettingHelper.NEW_WINDOW_LIVEROOM, true))
            {
                Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().Title = $"{Title} - {SiteName}";
            }
        }

        private void AddFavorite()
        {
            if (Site == null || RoomID == null || RoomID == "0" || RoomID == "") return;
            DatabaseHelper.AddFavorite(new Models.FavoriteItem()
            {
                Photo = Photo,
                RoomID = RoomID,
                SiteName = Site.Name,
                UserName = Name
            });
            IsFavorite = true;
            MessageCenter.UpdateFavorite();
        }
        private void RemoveFavorite()
        {
            if (FavoriteID == null)
            {
                return;
            }
            DatabaseHelper.DeleteFavorite(FavoriteID.Value);
            IsFavorite = false;
            MessageCenter.UpdateFavorite();
        }

        public async void LoadPlayUrl()
        {
            try
            {
                if (!isActive)
                {
                    return;
                }
                var data = await Site.GetPlayUrls(detail, CurrentQuality);
                if (!isActive)
                {
                    return;
                }
                if (data.Count == 0)
                {
                    LogHelper.Log($"加载播放地址失败: 返回空列表。站点:{Site?.Name} 房间ID:{RoomID} 清晰度:{CurrentQuality?.Quality}", LogType.ERROR);
                    Utils.ShowMessageToast("加载播放地址失败");
                    return;
                }
                var debugSnapshot = BuildPlayUrlDebug(data);
                if (!string.IsNullOrEmpty(debugSnapshot))
                {
                    LogHelper.Log(debugSnapshot, LogType.DEBUG);
                }
                List<PlayurlLine> ls = new List<PlayurlLine>();
                for (int i = 0; i < data.Count; i++)
                {
                    ls.Add(new PlayurlLine()
                    {
                        Name = $"线路{i + 1}",
                        Url = data[i],
                        Codec = DetectCodecLabel(data[i], Site?.Name)
                    });
                }

                allLines = ls;
                UpdateAvailableCodecs();
            }
            catch (Exception ex)
            {
                LogHelper.Log($"加载播放地址失败。站点:{Site?.Name} 房间ID:{RoomID} 清晰度:{CurrentQuality?.Quality}", LogType.ERROR, ex);
                Utils.ShowMessageToast("加载播放地址失败");
            }




        }

        private string BuildPlayUrlDebug(List<string> urls)
        {
            if (urls == null)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"播放地址生成:");
            sb.AppendLine($"站点: {Site?.Name}");
            sb.AppendLine($"房间ID: {RoomID}");
            sb.AppendLine($"清晰度: {CurrentQuality?.Quality}");
            sb.AppendLine($"数量: {urls.Count}");
            var index = 1;
            foreach (var url in urls)
            {
                sb.AppendLine(BuildSingleUrlDebug(url, index));
                index++;
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildSingleUrlDebug(string url, int index)
        {
            if (string.IsNullOrEmpty(url))
            {
                return $"线路{index}: 空";
            }
            try
            {
                var uri = new Uri(url);
                var sb = new StringBuilder();
                sb.AppendLine($"线路{index}: {uri.Scheme}://{uri.Host}{uri.AbsolutePath}");
                sb.AppendLine($"长度: {url.Length}");
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    var decoder = new WwwFormUrlDecoder(uri.Query);
                    foreach (var item in decoder)
                    {
                        if (string.Equals(item.Name, "wsTime", StringComparison.OrdinalIgnoreCase))
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
                        else if (string.Equals(item.Name, "expires", StringComparison.OrdinalIgnoreCase) ||
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
                        else if (string.Equals(item.Name, "wsSecret", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"{item.Name}Len={item.Value?.Length ?? 0}");
                        }
                        else if (string.Equals(item.Name, "fm", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"{item.Name}Len={item.Value?.Length ?? 0}");
                        }
                        else if (string.Equals(item.Name, "codec", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "ratio", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "cdn", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "seqid", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "uid", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "uuid", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "t", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "sv", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "fs", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(item.Name, "ctype", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"{item.Name}={item.Value}");
                        }
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"线路{index}: 解析失败 {ex.GetType().FullName} {ex.Message}";
            }
        }

        private void UpdateAvailableCodecs()
        {
            var codecs = new List<string>();
            if (allLines != null)
            {
                foreach (var line in allLines)
                {
                    if (string.IsNullOrWhiteSpace(line?.Codec))
                    {
                        continue;
                    }
                    if (codecs.FirstOrDefault(x => string.Equals(x, line.Codec, StringComparison.OrdinalIgnoreCase)) == null)
                    {
                        codecs.Add(line.Codec);
                    }
                }
            }

            var ordered = new List<string>();
            foreach (var codec in GetPreferredCodecOrder())
            {
                if (codecs.Any(x => string.Equals(x, codec, StringComparison.OrdinalIgnoreCase)))
                {
                    ordered.Add(codec);
                }
            }
            foreach (var codec in codecs)
            {
                if (ordered.FirstOrDefault(x => string.Equals(x, codec, StringComparison.OrdinalIgnoreCase)) == null)
                {
                    ordered.Add(codec);
                }
            }

            var newList = new List<string>() { "全部" };
            newList.AddRange(ordered);
            AvailableCodecs = newList;

            var hasSelected = !string.IsNullOrWhiteSpace(SelectedCodec)
                && newList.FirstOrDefault(x => string.Equals(x, SelectedCodec, StringComparison.OrdinalIgnoreCase)) != null;

            if (!hasSelected || string.Equals(SelectedCodec, "全部", StringComparison.OrdinalIgnoreCase))
            {
                SelectedCodec = GetDefaultCodecSelection(newList);
                return;
            }

            ApplyCodecFilter();
        }

        private IEnumerable<string> GetPreferredCodecOrder()
        {
            if (string.Equals(Site?.Name, "哔哩哔哩直播", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "AVC", "HEVC", "AV1" };
            }

            return new[] { "HEVC", "AVC", "AV1" };
        }

        private string GetDefaultCodecSelection(IEnumerable<string> codecList)
        {
            if (codecList != null)
            {
                foreach (var codec in GetPreferredCodecOrder())
                {
                    if (codecList.Any(x => string.Equals(x, codec, StringComparison.OrdinalIgnoreCase)))
                    {
                        return codec;
                    }
                }
            }

            var fallback = codecList?.FirstOrDefault(x => !string.Equals(x, "全部", StringComparison.OrdinalIgnoreCase));
            return fallback ?? "全部";
        }

        private void ApplyCodecFilter()
        {
            if (allLines == null)
            {
                Lines = null;
                return;
            }

            IEnumerable<PlayurlLine> filtered = allLines;
            if (!string.IsNullOrWhiteSpace(SelectedCodec) && !string.Equals(SelectedCodec, "全部", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(x => string.Equals(x?.Codec, SelectedCodec, StringComparison.OrdinalIgnoreCase));
            }
            var newLines = filtered.ToList();
            Lines = newLines;

            if (newLines.Count == 0)
            {
                return;
            }
            if (CurrentLine == null || !newLines.Contains(CurrentLine))
            {
                CurrentLine = newLines[0];
            }
        }

        private static string DetectCodecLabel(string url, string siteName)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (url.IndexOf("prohevc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "HEVC";
            }
            if (url.IndexOf("proav1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "AV1";
            }

            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
                {
                    var decoder = new WwwFormUrlDecoder(uri.Query);
                    foreach (var item in decoder)
                    {
                        if (!string.Equals(item.Name, "codec", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        switch (item.Value?.Trim())
                        {
                            case "0":
                                return "AVC";
                            case "1":
                                return "HEVC";
                            case "2":
                                return "AV1";
                        }
                    }
                }
            }
            catch
            {
            }

            if (url.IndexOf(".flv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "AVC";
            }
            if (string.Equals(siteName, "抖音直播", StringComparison.OrdinalIgnoreCase)
                && url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "AVC";
            }

            return null;
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

        public async void LoadSuperChat()
        {
            try
            {
                if (!isActive || Site == null)
                {
                    return;
                }
                var data = await Site.GetSuperChatMessages(RoomID);
                if (!isActive)
                {
                    return;
                }
                if (data.Count > 0)
                {
                    foreach (var item in data)
                    {
                        SuperChatMessages.Insert(0, new SuperChatItem(item, KeepSC ? false : true));
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log("加载SC失败", LogType.ERROR, ex);
                Utils.ShowMessageToast("加载SC失败");
            }

        }
        private async void LiveDanmaku_OnClose(object sender, string e)
        {
            var dispatcher = Dispatcher;
            if (!isActive || dispatcher == null)
            {
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!isActive)
                {
                    return;
                }
                Messages.Add(new LiveMessage()
                {
                    Type = LiveMessageType.Chat,
                    UserName = "系统",
                    Message = string.IsNullOrEmpty(e) ? "弹幕连接已断开" : $"弹幕连接已断开: {e}"
                });
            });

            if (!isActive)
            {
                return;
            }

            StartDanmakuReconnect(useInitialFailureMessage: false);
        }

        private void StartDanmakuReconnect(bool useInitialFailureMessage)
        {
            if (!isActive || Dispatcher == null)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref reconnectInProgress, 1, 0) != 0)
            {
                return;
            }

            _ = ReconnectDanmakuAsync(useInitialFailureMessage);
        }

        private async Task ReconnectDanmakuAsync(bool useInitialFailureMessage)
        {
            CancelDanmakuReconnect();
            var cts = new System.Threading.CancellationTokenSource();
            danmakuReconnectCts = cts;
            var token = cts.Token;

            try
            {
                while (isActive && !token.IsCancellationRequested)
                {
                    if (danmakuReconnectAttempt >= MaxDanmakuReconnectAttempts)
                    {
                        var dispatcher = Dispatcher;
                        if (dispatcher == null)
                        {
                            return;
                        }
                        await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (!isActive)
                            {
                                return;
                            }
                            Messages.Add(new LiveMessage()
                            {
                                Type = LiveMessageType.Chat,
                                UserName = "系统",
                                Message = "弹幕重连已达上限，请手动刷新"
                            });
                        });
                        RaiseReconnectStatus("弹幕", false, danmakuReconnectAttempt, MaxDanmakuReconnectAttempts, "弹幕重连失败");
                        return;
                    }

                    var delay = DanmakuReconnectDelays[Math.Min(danmakuReconnectAttempt, DanmakuReconnectDelays.Length - 1)];
                    danmakuReconnectAttempt++;
                    var reconnectMessage = useInitialFailureMessage
                        ? $"弹幕启动失败，{delay.TotalSeconds:F0}s 后重试连接 ({danmakuReconnectAttempt}/{MaxDanmakuReconnectAttempts})"
                        : $"弹幕连接异常，{delay.TotalSeconds:F0}s 后重连 ({danmakuReconnectAttempt}/{MaxDanmakuReconnectAttempts})";
                    RaiseReconnectStatus("弹幕", true, danmakuReconnectAttempt, MaxDanmakuReconnectAttempts,
                        reconnectMessage);
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    if (!isActive || token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        await CleanupOldDanmakuAsync();
                        if (!isActive || token.IsCancellationRequested)
                        {
                            return;
                        }

                        LogHelper.Log($"弹幕重连: 站点={Site?.Name} 房间={RoomID} 第{danmakuReconnectAttempt}次", LogType.DEBUG);
                        var newDanmaku = Site.GetDanmaku();
                        newDanmaku.NewMessage += LiveDanmaku_NewMessage;
                        newDanmaku.OnClose += LiveDanmaku_OnClose;
                        LiveDanmaku = newDanmaku;
                        await newDanmaku.Start(danmakuArgs);

                        danmakuReconnectAttempt = 0;
                        System.Threading.Interlocked.Exchange(ref reconnectInProgress, 0);
                        var dispatcher = Dispatcher;
                        if (dispatcher == null)
                        {
                            return;
                        }
                        await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            if (!isActive)
                            {
                                return;
                            }
                            Messages.Add(new LiveMessage()
                            {
                                Type = LiveMessageType.Chat,
                                UserName = "系统",
                                Message = "弹幕已重新连接"
                            });
                        });
                        RaiseReconnectStatus("弹幕", false, 0, MaxDanmakuReconnectAttempts, "弹幕已重连");
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogHelper.Log($"弹幕重连失败(第{danmakuReconnectAttempt}次)", LogType.ERROR, ex);
                    }
                }
            }
            finally
            {
                if (danmakuReconnectCts == cts)
                {
                    danmakuReconnectCts = null;
                }
                cts.Dispose();
                if (!isActive || token.IsCancellationRequested || danmakuReconnectAttempt >= MaxDanmakuReconnectAttempts)
                {
                    System.Threading.Interlocked.Exchange(ref reconnectInProgress, 0);
                }
            }
        }

        private async Task CleanupOldDanmakuAsync()
        {
            var old = LiveDanmaku;
            if (old == null)
            {
                return;
            }
            old.NewMessage -= LiveDanmaku_NewMessage;
            old.OnClose -= LiveDanmaku_OnClose;
            LiveDanmaku = null;
            try
            {
                await old.Stop();
            }
            catch
            {
            }
        }

        private void CancelDanmakuReconnect()
        {
            var cts = danmakuReconnectCts;
            danmakuReconnectCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private void RaiseReconnectStatus(string source, bool inProgress, int attempt, int max, string message)
        {
            var handler = ReconnectStatusChanged;
            var dispatcher = Dispatcher;
            if (handler == null || dispatcher == null || !isActive)
            {
                return;
            }
            _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!isActive)
                {
                    return;
                }
                handler(this, new ReconnectStatus
                {
                    Source = source,
                    IsReconnecting = inProgress,
                    Attempt = attempt,
                    MaxAttempt = max,
                    Message = message
                });
            });
        }
        public CoreDispatcher Dispatcher { get; set; }
        private async void LiveDanmaku_NewMessage(object sender, LiveMessage e)
        {
            var dispatcher = Dispatcher;
            if (e == null || !isActive || dispatcher == null)
            {
                return;
            }
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!isActive)
                {
                    return;
                }
                if (e.Type == LiveMessageType.Online)
                {
                    ApplyAudienceMetricUpdate(e);
                    return;
                }
                if (e.Type == LiveMessageType.SuperChat)
                {
                    SuperChatMessages.Insert(0, new SuperChatItem(e.Data as LiveSuperChatMessage, KeepSC ? false : true));
                    return;
                }
                if (e.Type == LiveMessageType.Chat)
                {
                    if (Messages.Count >= MessageCleanCount)
                    {
                        Messages.RemoveAt(0);
                        //Messages.Clear();
                    }
                    if (settingVM.ShieldWords != null && settingVM.ShieldWords.Count > 0)
                    {
                        if (settingVM.ShieldWords.FirstOrDefault(x => e.Message.Contains(x)) != null) return;
                    }
                    if (!Utils.IsXbox)
                    {
                        Messages.Add(e);
                    }

                    AddDanmaku?.Invoke(this, e);
                    return;
                }
            });
        }

        private void LogRoomDetail(LiveRoomDetail roomDetail)
        {
            try
            {
                if (roomDetail == null)
                {
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("直播间详情加载:");
                sb.AppendLine($"站点: {Site?.Name}");
                sb.AppendLine($"请求房间ID: {RoomId}");
                sb.AppendLine($"实际房间ID: {roomDetail.RoomID}");
                sb.AppendLine($"标题: {roomDetail.Title}");
                sb.AppendLine($"主播: {roomDetail.UserName}");
                sb.AppendLine($"开播状态: {roomDetail.Status}");
                sb.AppendLine($"兼容显示值 Online: {roomDetail.Online}");
                sb.AppendLine($"ViewerCount: {(roomDetail.ViewerCount.HasValue ? roomDetail.ViewerCount.Value.ToString() : "null")}");
                sb.AppendLine($"ViewerCountSource: {roomDetail.ViewerCountSource ?? "null"}");
                sb.AppendLine($"VipCount: {(roomDetail.VipCount.HasValue ? roomDetail.VipCount.Value.ToString() : "null")}");
                sb.AppendLine($"VipCountSource: {roomDetail.VipCountSource ?? "null"}");
                sb.AppendLine($"Popularity: {(roomDetail.Popularity.HasValue ? roomDetail.Popularity.Value.ToString() : "null")}");
                sb.AppendLine($"PopularitySource: {roomDetail.PopularitySource ?? "null"}");
                sb.AppendLine($"最终显示值 DisplayMetric: {(roomDetail.ViewerCount ?? roomDetail.VipCount ?? roomDetail.Popularity ?? roomDetail.Online)}");
                sb.AppendLine($"链接: {roomDetail.Url}");
                if (!string.IsNullOrWhiteSpace(roomDetail.Introduction))
                {
                    sb.AppendLine($"简介长度: {roomDetail.Introduction.Length}");
                }
                if (!string.IsNullOrWhiteSpace(roomDetail.Notice))
                {
                    sb.AppendLine($"公告长度: {roomDetail.Notice.Length}");
                }

                var dataSummary = BuildSafeObjectSummary("Data", roomDetail.Data);
                if (!string.IsNullOrWhiteSpace(dataSummary))
                {
                    sb.AppendLine(dataSummary);
                }

                var danmakuSummary = BuildSafeObjectSummary("DanmakuData", roomDetail.DanmakuData);
                if (!string.IsNullOrWhiteSpace(danmakuSummary))
                {
                    sb.AppendLine(danmakuSummary);
                }

                LogHelper.Log(sb.ToString().TrimEnd(), LogType.DEBUG);
            }
            catch (Exception ex)
            {
                LogHelper.Log("直播间详情日志生成失败", LogType.ERROR, ex);
            }
        }

        private string BuildSafeObjectSummary(string name, object value)
        {
            if (value == null)
            {
                return $"{name}: null";
            }

            if (value is string str)
            {
                if (Site?.Name == "斗鱼直播" && name == "Data")
                {
                    return $"{name}: string len={str.Length} (已隐藏签名原文)";
                }
                return $"{name}: string len={str.Length}";
            }

            if (Site?.Name == "虎牙直播" && name == "Data")
            {
                var type = value.GetType();
                var lines = ReadCollectionCount(type.GetProperty("Lines"), value);
                var bitRates = ReadCollectionCount(type.GetProperty("BitRates"), value);
                var uid = type.GetProperty("Uid")?.GetValue(value)?.ToString();
                var uuid = type.GetProperty("UUid")?.GetValue(value)?.ToString();
                return $"{name}: {type.FullName} lines={lines} bitRates={bitRates} uid={uid} uuid={uuid}";
            }

            return $"{name}: {BuildObjectPropertySummary(value, 6)}";
        }

        private static string BuildObjectPropertySummary(object value, int maxProperties)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid)
            {
                return $"{type.FullName} value={value}";
            }

            var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(x => x.CanRead && x.GetIndexParameters().Length == 0)
                .Take(maxProperties)
                .ToList();
            if (props.Count == 0)
            {
                return type.FullName;
            }

            var parts = new List<string>();
            foreach (var prop in props)
            {
                if (IsSensitivePropertyName(prop.Name))
                {
                    continue;
                }

                object propValue;
                try
                {
                    propValue = prop.GetValue(value);
                }
                catch
                {
                    continue;
                }

                if (propValue == null)
                {
                    parts.Add($"{prop.Name}=null");
                    continue;
                }

                if (propValue is string str)
                {
                    parts.Add($"{prop.Name}=string(len={str.Length})");
                    continue;
                }

                var count = TryGetCollectionCount(propValue);
                if (count.HasValue)
                {
                    parts.Add($"{prop.Name}=count({count.Value})");
                    continue;
                }

                if (propValue.GetType().IsPrimitive || propValue is decimal)
                {
                    parts.Add($"{prop.Name}={propValue}");
                    continue;
                }

                parts.Add($"{prop.Name}={propValue.GetType().Name}");
            }

            return parts.Count == 0 ? type.FullName : $"{type.FullName} {string.Join(", ", parts)}";
        }

        private static int ReadCollectionCount(PropertyInfo property, object owner)
        {
            if (property == null || owner == null)
            {
                return -1;
            }

            try
            {
                return TryGetCollectionCount(property.GetValue(owner)) ?? -1;
            }
            catch
            {
                return -1;
            }
        }

        private static int? TryGetCollectionCount(object value)
        {
            if (value == null || value is string)
            {
                return null;
            }

            if (value is System.Collections.ICollection collection)
            {
                return collection.Count;
            }

            return null;
        }

        private static bool IsSensitivePropertyName(string propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            return propertyName.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("sign", StringComparison.OrdinalIgnoreCase) >= 0
                || propertyName.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task StopAsync()
        {
            isActive = false;
            CancelDanmakuReconnect();
            System.Threading.Interlocked.Exchange(ref reconnectInProgress, 0);
            danmakuReconnectAttempt = 0;
            StopSCTimer();
            Messages.Clear();
            SuperChatMessages.Clear();
            if (LiveDanmaku != null)
            {
                LiveDanmaku.NewMessage -= LiveDanmaku_NewMessage;
                LiveDanmaku.OnClose -= LiveDanmaku_OnClose;
                try
                {
                    await LiveDanmaku.Stop();
                }
                catch
                {
                }
                LiveDanmaku = null;
            }

            allLines = null;
            Lines = null;
            Qualities = null;
            AvailableCodecs = new List<string>() { "全部" };
            CurrentLine = null;
            currentQuality = null;
            RoomId = null;
            detail = null;
            Site = null;
            FavoriteID = null;
            SiteName = null;
            Name = null;
            Title = null;
            Photo = "ms-appx:///Assets/Placeholder/Placeholder1x1.png";
            Living = false;
            IsFavorite = false;
            ViewerCount = null;
            VipCount = null;
            Popularity = null;
            allowPopularityFallback = true;
            Loading = false;

        }

        public void ReleaseViewReferences()
        {
            ChangedPlayUrl = null;
            AddDanmaku = null;
            ReconnectStatusChanged = null;
            Dispatcher = null;
        }
    }

    public class PlayurlLine
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public string Codec { get; set; }
    }
    public class ReconnectStatus
    {
        public string Source { get; set; }
        public bool IsReconnecting { get; set; }
        public int Attempt { get; set; }
        public int MaxAttempt { get; set; }
        public string Message { get; set; }
    }
    public class SettingsItem<T>
    {

        public string Name { get; set; }
        public T Value { get; set; }
    }

    public class SuperChatItem : LiveSuperChatMessage, INotifyPropertyChanged
    {
        public SuperChatItem(LiveSuperChatMessage message, bool showCountdown)
        {
            UserName = message.UserName;
            Face = message.Face;
            Message = message.Message;
            Price = message.Price;
            StartTime = message.StartTime;
            EndTime = message.EndTime;
            BackgroundColor = message.BackgroundColor;
            BackgroundBottomColor = message.BackgroundBottomColor;
            CountdownTime = Convert.ToInt32(EndTime.Subtract(DateTime.Now).TotalSeconds);
            ShowCountdown = showCountdown;
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void DoPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        private int _countdownTime = 0;
        public int CountdownTime
        {
            get { return _countdownTime; }
            set { _countdownTime = value; DoPropertyChanged("CountdownTime"); }
        }

        public string StartTimeStr
        {
            get
            {
                return StartTime.ToString("HH:mm:ss");
            }
        }

        private bool showCountdown = false;

        public bool ShowCountdown
        {
            get { return showCountdown; }
            set { showCountdown = value; DoPropertyChanged("ShowCountdown"); }
        }


    }
}
