using AllLive.UWP.Helper;
using CoreConfig = AllLive.Core.Helper.CoreConfig;
using AllLive.UWP.Models;
using AllLive.UWP.ViewModels;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.UI.Xaml.Controls;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// https://go.microsoft.com/fwlink/?LinkId=234238 上介绍了“空白页”项模板

namespace AllLive.UWP.Views
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        readonly SettingVM settingVM;
        private bool _isUpdatingDouyinCookie;
        private bool isBiliAccountChangedSubscribed;
        public SettingsPage()
        {
            settingVM = new SettingVM();
            this.InitializeComponent();
            if (Utils.IsXbox)
            {
                SettingsPaneDiaplsyMode.Visibility = Visibility.Collapsed;
                SettingsMouseClosePage.Visibility = Visibility.Collapsed;
                SettingsFontSize.Visibility = Visibility.Collapsed;
                SettingsAutoClean.Visibility = Visibility.Collapsed;
                SettingsXboxMode.Visibility = Visibility.Visible;
                SettingsNewWindow.Visibility = Visibility.Collapsed;
            }
            SubscribeBiliAccountChanged();
            LoadUI();

        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            UnsubscribeBiliAccountChanged();
            base.OnNavigatedFrom(e);
        }

        private void SubscribeBiliAccountChanged()
        {
            if (isBiliAccountChangedSubscribed)
            {
                return;
            }
            BiliAccount.Instance.OnAccountChanged += BiliAccount_OnAccountChanged;
            isBiliAccountChangedSubscribed = true;
        }

        private void UnsubscribeBiliAccountChanged()
        {
            if (!isBiliAccountChangedSubscribed)
            {
                return;
            }
            BiliAccount.Instance.OnAccountChanged -= BiliAccount_OnAccountChanged;
            isBiliAccountChangedSubscribed = false;
        }

        private void BiliAccount_OnAccountChanged(object sender, EventArgs e)
        {
            UpdateBiliAccountUi();
        }

        private void UpdateBiliAccountUi()
        {
            if (BiliAccount.Instance.Logined)
            {
                txtBili.Text = string.IsNullOrWhiteSpace(BiliAccount.Instance.UserName)
                    ? "已登录"
                    : $"已登录：{BiliAccount.Instance.UserName}";
                BtnLoginBili.Visibility = Visibility.Collapsed;
                BtnLogoutBili.Visibility = Visibility.Visible;
            }
            else
            {
                txtBili.Text = "登录可享受高清直播";
                BtnLoginBili.Visibility = Visibility.Visible;
                BtnLogoutBili.Visibility = Visibility.Collapsed;
            }
        }

        private void LoadUI()
        {
            //主题
            cbTheme.SelectedIndex = ThemeHelper.GetSavedTheme();
            cbTheme.Loaded += new RoutedEventHandler((sender, e) =>
            {
                cbTheme.SelectionChanged += new SelectionChangedEventHandler((obj, args) =>
                {
                    var theme = ThemeHelper.NormalizeTheme(cbTheme.SelectedIndex);
                    if (cbTheme.SelectedIndex != theme)
                    {
                        cbTheme.SelectedIndex = theme;
                        return;
                    }
                    SettingHelper.SetValue(SettingHelper.THEME, theme);
                    ThemeHelper.Apply(theme, Window.Current?.Content as FrameworkElement);
                });
            });

            // xbox操作模式
            cbXboxMode.SelectedIndex = SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0);
            cbXboxMode.Loaded += new RoutedEventHandler((sender, e) =>
            {
                cbXboxMode.SelectionChanged += new SelectionChangedEventHandler((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.XBOX_MODE, cbXboxMode.SelectedIndex);
                    Utils.ShowMessageToast("重启应用生效");
                });
            });

            //导航栏显示模式
            cbPaneDisplayMode.SelectedIndex = SettingHelper.GetValue<int>(SettingHelper.PANE_DISPLAY_MODE, 0);
            cbPaneDisplayMode.Loaded += new RoutedEventHandler((sender, e) =>
            {
                cbPaneDisplayMode.SelectionChanged += new SelectionChangedEventHandler((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.PANE_DISPLAY_MODE, cbPaneDisplayMode.SelectedIndex);
                    MessageCenter.UpdatePanelDisplayMode();
                });
            });

            //鼠标侧键返回
            swMouseClosePage.IsOn = SettingHelper.GetValue<bool>(SettingHelper.MOUSE_BACK, true);
            swMouseClosePage.Loaded += new RoutedEventHandler((sender, e) =>
            {
                swMouseClosePage.Toggled += new RoutedEventHandler((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.MOUSE_BACK, swMouseClosePage.IsOn);
                });
            });

            //日志开关
            swLogEnabled.IsOn = SettingHelper.GetValue<bool>(SettingHelper.LOG_ENABLED, false);
            LogHelper.SetEnabled(swLogEnabled.IsOn);
            swLogEnabled.Toggled += new RoutedEventHandler((sender, e) =>
            {
                SettingHelper.SetValue(SettingHelper.LOG_ENABLED, swLogEnabled.IsOn);
                LogHelper.SetEnabled(swLogEnabled.IsOn);
            });

            //关注列表自动刷新间隔
            var favoriteRefreshMinutes = SettingHelper.GetValue<int>(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, 5);
            if (favoriteRefreshMinutes < 1)
            {
                favoriteRefreshMinutes = 5;
            }
            numFavoriteRefreshInterval.Value = favoriteRefreshMinutes;
            numFavoriteRefreshInterval.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numFavoriteRefreshInterval.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    if (double.IsNaN(args.NewValue))
                    {
                        return;
                    }
                    var minutes = Convert.ToInt32(args.NewValue);
                    if (minutes < 1)
                    {
                        minutes = 1;
                    }
                    SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, minutes);
                });
            });
            //视频解码
            cbDecoder.SelectedIndex = ClampInt(SettingHelper.GetValue<int>(SettingHelper.VIDEO_DECODER, 1), 0, 3);
            cbDecoder.Loaded += new RoutedEventHandler((sender, e) =>
            {
                cbDecoder.SelectionChanged += new SelectionChangedEventHandler((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.VIDEO_DECODER, cbDecoder.SelectedIndex);
                });
            });

            swDouyuSignService.IsOn = SettingHelper.GetValue<bool>(SettingHelper.DOUYU_SIGN_ENABLED, true);
            swDouyuSignService.Toggled += new RoutedEventHandler((obj, args) =>
            {
                SettingHelper.SetValue(SettingHelper.DOUYU_SIGN_ENABLED, swDouyuSignService.IsOn);
                txtDouyuSignUrl.IsEnabled = swDouyuSignService.IsOn;
                ApplyDouyuSignServiceSetting(txtDouyuSignUrl.Text, swDouyuSignService.IsOn);
                _ = UpdateDouyuSignStatusAsync();
            });

            //斗鱼签名服务地址
            txtDouyuSignUrl.Text = SettingHelper.GetValue<string>(SettingHelper.DOUYU_SIGN_URL, SettingHelper.DOUYU_SIGN_URL_DEFAULT);
            txtDouyuSignUrl.IsEnabled = swDouyuSignService.IsOn;
            ApplyDouyuSignServiceSetting(txtDouyuSignUrl.Text, swDouyuSignService.IsOn);
            _ = UpdateDouyuSignStatusAsync();
            txtDouyuSignUrl.Loaded += new RoutedEventHandler((sender, e) =>
            {
                txtDouyuSignUrl.TextChanged += new TextChangedEventHandler((obj, args) =>
                {
                    var value = txtDouyuSignUrl.Text?.Trim() ?? "";
                    SettingHelper.SetValue(SettingHelper.DOUYU_SIGN_URL, value);
                    ApplyDouyuSignServiceSetting(value, swDouyuSignService.IsOn);
                });
            });

            swDouyinSignService.IsOn = SettingHelper.GetValue<bool>(SettingHelper.DOUYIN_SIGN_ENABLED, true);
            swDouyinSignService.Toggled += new RoutedEventHandler((obj, args) =>
            {
                SettingHelper.SetValue(SettingHelper.DOUYIN_SIGN_ENABLED, swDouyinSignService.IsOn);
                txtDouyinSignUrl.IsEnabled = swDouyinSignService.IsOn;
                ApplyDouyinSignServiceSetting(txtDouyinSignUrl.Text, swDouyinSignService.IsOn);
                _ = UpdateDouyinSignStatusAsync();
            });

            txtDouyinSignUrl.Text = SettingHelper.GetValue<string>(SettingHelper.DOUYIN_SIGN_URL, SettingHelper.DOUYIN_SIGN_URL_DEFAULT);
            txtDouyinSignUrl.IsEnabled = swDouyinSignService.IsOn;
            ApplyDouyinSignServiceSetting(txtDouyinSignUrl.Text, swDouyinSignService.IsOn);
            _ = UpdateDouyinSignStatusAsync();
            txtDouyinSignUrl.Loaded += new RoutedEventHandler((sender, e) =>
            {
                txtDouyinSignUrl.TextChanged += new TextChangedEventHandler((obj, args) =>
                {
                    var value = txtDouyinSignUrl.Text?.Trim() ?? "";
                    SettingHelper.SetValue(SettingHelper.DOUYIN_SIGN_URL, value);
                    ApplyDouyinSignServiceSetting(value, swDouyinSignService.IsOn);
                });
            });

            numFontsize.Value = SettingHelper.GetValue<double>(SettingHelper.MESSAGE_FONTSIZE, 14.0);
            numFontsize.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numFontsize.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.MESSAGE_FONTSIZE, args.NewValue);
                });
            });

            //新窗口打开
            swNewWindow.IsOn = SettingHelper.GetValue<bool>(SettingHelper.NEW_WINDOW_LIVEROOM, true);
            swNewWindow.Loaded += new RoutedEventHandler((sender, e) =>
            {
                swNewWindow.Toggled += new RoutedEventHandler((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.NEW_WINDOW_LIVEROOM, swNewWindow.IsOn);
                });
            });
            //弹幕开关
            var state = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false);
            DanmuSettingState.IsOn = state;
            DanmuSettingState.Toggled += new RoutedEventHandler((e, args) =>
            {
                SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHOW, DanmuSettingState.IsOn);
            });

            // 保留醒目留言
            var keepSC = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true);
            SettingKeepSC.IsOn = keepSC;
            SettingKeepSC.Toggled += new RoutedEventHandler((e, args) =>
            {
                SettingHelper.SetValue(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, SettingKeepSC.IsOn);
            });

            //弹幕清理
            numCleanCount.Value = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, 200);
            numCleanCount.Loaded += new RoutedEventHandler((sender, e) =>
            {
                numCleanCount.ValueChanged += new TypedEventHandler<NumberBox, NumberBoxValueChangedEventArgs>((obj, args) =>
                {
                    SettingHelper.SetValue(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, Convert.ToInt32(args.NewValue));
                });
            });
            //弹幕关键词
            LiveDanmuSettingListWords.ItemsSource = settingVM.ShieldWords;

            // 抖音Cookie
            _ = LoadDouyinCookieAsync();
            txtDouyinCookie.Loaded += new RoutedEventHandler((sender, e) =>
            {
                txtDouyinCookie.TextChanged += new TextChangedEventHandler((obj, args) =>
                {
                    if (_isUpdatingDouyinCookie)
                    {
                        return;
                    }
                    var rawValue = txtDouyinCookie.Text ?? "";
                    var normalized = NormalizeDouyinCookieInput(rawValue);
                    var trimmed = TrimDouyinCookieForSettings(normalized);
                    SettingHelper.SetValue(SettingHelper.DOUYIN_COOKIE, trimmed);
                    ApplyDouyinCookieSetting(normalized);
                    _ = DouyinCookieStore.SaveAsync(normalized);
                    if (!string.Equals(rawValue, normalized, StringComparison.Ordinal))
                    {
                        _isUpdatingDouyinCookie = true;
                        txtDouyinCookie.Text = normalized;
                        txtDouyinCookie.SelectionStart = txtDouyinCookie.Text.Length;
                        _isUpdatingDouyinCookie = false;
                    }
                });
            });


            UpdateBiliAccountUi();
           
        }

        private class SettingsBackupData
        {
            public int Version { get; set; } = 1;
            public DateTimeOffset ExportedAt { get; set; }
            public int? Theme { get; set; }
            public int? XboxMode { get; set; }
            public int? PaneDisplayMode { get; set; }
            public bool? MouseBack { get; set; }
            public int? FavoriteAutoRefreshMinutes { get; set; }
            public bool? FavoriteHideOffline { get; set; }
            public bool? NewWindowLiveRoom { get; set; }
            public int? VideoDecoder { get; set; }
            public bool? DouyuSignEnabled { get; set; }
            public string DouyuSignUrl { get; set; }
            public bool? DouyinSignEnabled { get; set; }
            public string DouyinSignUrl { get; set; }
            public double? MessageFontSize { get; set; }
            public double? PlayerVolume { get; set; }
            public double? PlayerBrightness { get; set; }
            public double? RightDetailWidth { get; set; }
            public bool? LiveDanmuShow { get; set; }
            public bool? KeepSuperChat { get; set; }
            public int? DanmuCleanCount { get; set; }
            public int? LiveDanmuTopMargin { get; set; }
            public double? LiveDanmuArea { get; set; }
            public double? LiveDanmuFontZoom { get; set; }
            public int? LiveDanmuSpeed { get; set; }
            public bool? LiveDanmuBold { get; set; }
            public bool? LiveDanmuColourful { get; set; }
            public int? LiveDanmuBorderStyle { get; set; }
            public double? LiveDanmuOpacity { get; set; }
            public List<string> ShieldWords { get; set; }
            public bool? LogEnabled { get; set; }
            public bool? IgnoreBiliLoginTip { get; set; }
            public string BiliCookie { get; set; }
            public long? BiliUserId { get; set; }
            public string BiliUserName { get; set; }
            public string DouyinCookie { get; set; }
            public List<FavoriteJsonItem> Favorites { get; set; }
        }

        private static int ClampInt(int value, int min, int max)
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

        private static int NormalizeFavoriteRefreshMinutes(int? value)
        {
            var minutes = value ?? SettingHelper.GetValue<int>(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, 5);
            return minutes < 1 ? 1 : minutes;
        }

        private static int NormalizeCleanCount(int? value)
        {
            var count = value ?? SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, 200);
            return count < 40 ? 40 : count;
        }

        private static double NormalizeFontSize(double? value)
        {
            var size = value ?? SettingHelper.GetValue<double>(SettingHelper.MESSAGE_FONTSIZE, 14.0);
            return size < 10 ? 10 : size;
        }

        private static double ClampDouble(double value, double min, double max)
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

        private static string GetFavoriteSiteId(string siteName)
        {
            switch (siteName)
            {
                case "哔哩哔哩直播":
                    return "bilibili";
                case "斗鱼直播":
                    return "douyu";
                case "虎牙直播":
                    return "huya";
                case "抖音直播":
                    return "douyin";
                default:
                    return "";
            }
        }

        private static FavoriteJsonItem CreateFavoriteJsonItem(FavoriteItem item)
        {
            var siteId = GetFavoriteSiteId(item.SiteName);
            return new FavoriteJsonItem()
            {
                SiteId = siteId,
                Id = $"{siteId}_{item.RoomID}",
                RoomId = item.RoomID,
                UserName = item.UserName,
                Face = item.Photo,
                AddTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.M"),
                Sort = item.SortOrder
            };
        }

        private async Task<List<FavoriteJsonItem>> BuildFavoriteBackupAsync()
        {
            var favorites = await DatabaseHelper.GetFavorites();
            return favorites.Select(CreateFavoriteJsonItem).ToList();
        }

        private static void ApplyFavoriteBackup(IEnumerable<FavoriteJsonItem> items)
        {
            DatabaseHelper.DeleteFavorite();
            foreach (var item in items ?? Enumerable.Empty<FavoriteJsonItem>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.RoomId))
                {
                    continue;
                }
                var siteName = item.SiteName;
                if (string.IsNullOrWhiteSpace(siteName) || siteName == "未知")
                {
                    continue;
                }
                DatabaseHelper.AddFavorite(new FavoriteItem()
                {
                    SiteName = siteName,
                    RoomID = item.RoomId,
                    UserName = item.UserName,
                    Photo = item.Face,
                    SortOrder = item.Sort
                });
            }
            MessageCenter.UpdateFavorite();
        }

        private List<string> GetShieldWordsForBackup()
        {
            if (settingVM?.ShieldWords == null)
            {
                var raw = SettingHelper.GetValue<string>(SettingHelper.LiveDanmaku.SHIELD_WORD, "[]");
                return JsonConvert.DeserializeObject<List<string>>(raw) ?? new List<string>();
            }
            return settingVM.ShieldWords.ToList();
        }

        private void ApplyShieldWords(IEnumerable<string> words)
        {
            var list = words?.Where(word => !string.IsNullOrWhiteSpace(word))
                .Select(word => word.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            settingVM.ShieldWords.Clear();
            foreach (var word in list)
            {
                settingVM.ShieldWords.Add(word);
            }
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHIELD_WORD, JsonConvert.SerializeObject(settingVM.ShieldWords));
        }

        private async Task<SettingsBackupData> BuildSettingsBackupAsync()
        {
            var cookie = await DouyinCookieStore.LoadAsync();
            if (string.IsNullOrWhiteSpace(cookie))
            {
                cookie = SettingHelper.GetValue<string>(SettingHelper.DOUYIN_COOKIE, "");
            }
            var favorites = await BuildFavoriteBackupAsync();
            return new SettingsBackupData
            {
                ExportedAt = DateTimeOffset.Now,
                Theme = SettingHelper.GetValue<int>(SettingHelper.THEME, 0),
                XboxMode = SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0),
                PaneDisplayMode = SettingHelper.GetValue<int>(SettingHelper.PANE_DISPLAY_MODE, 0),
                MouseBack = SettingHelper.GetValue<bool>(SettingHelper.MOUSE_BACK, true),
                FavoriteAutoRefreshMinutes = NormalizeFavoriteRefreshMinutes(null),
                FavoriteHideOffline = SettingHelper.GetValue<bool>(SettingHelper.FAVORITE_HIDE_OFFLINE, false),
                NewWindowLiveRoom = SettingHelper.GetValue<bool>(SettingHelper.NEW_WINDOW_LIVEROOM, true),
                VideoDecoder = SettingHelper.GetValue<int>(SettingHelper.VIDEO_DECODER, 1),
                DouyuSignEnabled = SettingHelper.GetValue<bool>(SettingHelper.DOUYU_SIGN_ENABLED, true),
                DouyuSignUrl = SettingHelper.GetValue<string>(SettingHelper.DOUYU_SIGN_URL, SettingHelper.DOUYU_SIGN_URL_DEFAULT),
                DouyinSignEnabled = SettingHelper.GetValue<bool>(SettingHelper.DOUYIN_SIGN_ENABLED, true),
                DouyinSignUrl = SettingHelper.GetValue<string>(SettingHelper.DOUYIN_SIGN_URL, SettingHelper.DOUYIN_SIGN_URL_DEFAULT),
                MessageFontSize = SettingHelper.GetValue<double>(SettingHelper.MESSAGE_FONTSIZE, 14.0),
                PlayerVolume = SettingHelper.GetValue<double>(SettingHelper.PLAYER_VOLUME, 1.0),
                PlayerBrightness = SettingHelper.GetValue<double>(SettingHelper.PLAYER_BRIGHTNESS, 0),
                RightDetailWidth = SettingHelper.GetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, 280),
                LiveDanmuShow = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false),
                KeepSuperChat = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true),
                DanmuCleanCount = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, 200),
                LiveDanmuTopMargin = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.TOP_MARGIN, 0),
                LiveDanmuArea = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.AREA, 1),
                LiveDanmuFontZoom = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, 1),
                LiveDanmuSpeed = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.SPEED, 10),
                LiveDanmuBold = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.BOLD, false),
                LiveDanmuColourful = SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, true),
                LiveDanmuBorderStyle = SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, 2),
                LiveDanmuOpacity = SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.OPACITY, 1.0),
                ShieldWords = GetShieldWordsForBackup(),
                LogEnabled = SettingHelper.GetValue<bool>(SettingHelper.LOG_ENABLED, false),
                IgnoreBiliLoginTip = SettingHelper.GetValue<bool>(SettingHelper.IGNORE_BILI_LOGIN_TIP, false),
                BiliCookie = SettingHelper.GetValue<string>(SettingHelper.BILI_COOKIE, ""),
                BiliUserId = SettingHelper.GetValue<long>(SettingHelper.BILI_USER_ID, 0L),
                BiliUserName = BiliAccount.Instance.UserName ?? "",
                DouyinCookie = cookie ?? "",
                Favorites = favorites
            };
        }

        private void ApplyBiliSettingsBackup(SettingsBackupData data)
        {
            if (data.BiliCookie == null)
            {
                return;
            }

            var cookie = data.BiliCookie?.Trim() ?? "";
            if (string.IsNullOrEmpty(cookie))
            {
                BiliAccount.Instance.Logout();
                return;
            }

            BiliAccount.Instance.ApplyLoginState(
                cookie,
                data.BiliUserId ?? 0L,
                data.BiliUserName ?? "");
        }

        private async Task ApplySettingsBackupAsync(SettingsBackupData data)
        {
            if (data == null)
            {
                return;
            }
            var theme = ThemeHelper.NormalizeTheme(data.Theme ?? SettingHelper.GetValue<int>(SettingHelper.THEME, 0));
            var xboxMode = ClampInt(data.XboxMode ?? SettingHelper.GetValue<int>(SettingHelper.XBOX_MODE, 0), 0, 1);
            var paneDisplayMode = ClampInt(data.PaneDisplayMode ?? SettingHelper.GetValue<int>(SettingHelper.PANE_DISPLAY_MODE, 0), 0, 1);
            var mouseBack = data.MouseBack ?? SettingHelper.GetValue<bool>(SettingHelper.MOUSE_BACK, true);
            var favoriteRefreshMinutes = NormalizeFavoriteRefreshMinutes(data.FavoriteAutoRefreshMinutes);
            var favoriteHideOffline = data.FavoriteHideOffline ?? SettingHelper.GetValue<bool>(SettingHelper.FAVORITE_HIDE_OFFLINE, false);
            var newWindowLiveRoom = data.NewWindowLiveRoom ?? SettingHelper.GetValue<bool>(SettingHelper.NEW_WINDOW_LIVEROOM, true);
            var videoDecoder = ClampInt(data.VideoDecoder ?? SettingHelper.GetValue<int>(SettingHelper.VIDEO_DECODER, 1), 0, 3);
            var douyuSignEnabled = data.DouyuSignEnabled ?? SettingHelper.GetValue<bool>(SettingHelper.DOUYU_SIGN_ENABLED, true);
            var douyuSignUrl = data.DouyuSignUrl ?? SettingHelper.GetValue<string>(SettingHelper.DOUYU_SIGN_URL, SettingHelper.DOUYU_SIGN_URL_DEFAULT);
            var douyinSignEnabled = data.DouyinSignEnabled ?? SettingHelper.GetValue<bool>(SettingHelper.DOUYIN_SIGN_ENABLED, true);
            var douyinSignUrl = data.DouyinSignUrl ?? SettingHelper.GetValue<string>(SettingHelper.DOUYIN_SIGN_URL, SettingHelper.DOUYIN_SIGN_URL_DEFAULT);
            var messageFontSize = NormalizeFontSize(data.MessageFontSize);
            var playerVolume = ClampDouble(data.PlayerVolume ?? SettingHelper.GetValue<double>(SettingHelper.PLAYER_VOLUME, 1.0), 0, 1);
            var playerBrightness = ClampDouble(data.PlayerBrightness ?? SettingHelper.GetValue<double>(SettingHelper.PLAYER_BRIGHTNESS, 0), 0, 1);
            var rightDetailWidth = Math.Max(180, data.RightDetailWidth ?? SettingHelper.GetValue<double>(SettingHelper.RIGHT_DETAIL_WIDTH, 280));
            var liveDanmuShow = data.LiveDanmuShow ?? SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.SHOW, false);
            var keepSuperChat = data.KeepSuperChat ?? SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, true);
            var danmuCleanCount = NormalizeCleanCount(data.DanmuCleanCount);
            var liveDanmuTopMargin = Math.Max(0, data.LiveDanmuTopMargin ?? SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.TOP_MARGIN, 0));
            var liveDanmuArea = ClampDouble(data.LiveDanmuArea ?? SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.AREA, 1), 0, 1);
            var liveDanmuFontZoom = Math.Max(0.1, data.LiveDanmuFontZoom ?? SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.FONT_ZOOM, 1));
            var liveDanmuSpeed = Math.Max(1, data.LiveDanmuSpeed ?? SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.SPEED, 10));
            var liveDanmuBold = data.LiveDanmuBold ?? SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.BOLD, false);
            var liveDanmuColourful = data.LiveDanmuColourful ?? SettingHelper.GetValue<bool>(SettingHelper.LiveDanmaku.COLOURFUL, true);
            var liveDanmuBorderStyle = ClampInt(data.LiveDanmuBorderStyle ?? SettingHelper.GetValue<int>(SettingHelper.LiveDanmaku.BORDER_STYLE, 2), 0, 2);
            var liveDanmuOpacity = ClampDouble(data.LiveDanmuOpacity ?? SettingHelper.GetValue<double>(SettingHelper.LiveDanmaku.OPACITY, 1.0), 0, 1);
            var logEnabled = data.LogEnabled ?? SettingHelper.GetValue<bool>(SettingHelper.LOG_ENABLED, false);
            var ignoreBiliLoginTip = data.IgnoreBiliLoginTip ?? SettingHelper.GetValue<bool>(SettingHelper.IGNORE_BILI_LOGIN_TIP, false);

            SettingHelper.SetValue(SettingHelper.THEME, theme);
            SettingHelper.SetValue(SettingHelper.XBOX_MODE, xboxMode);
            SettingHelper.SetValue(SettingHelper.PANE_DISPLAY_MODE, paneDisplayMode);
            SettingHelper.SetValue(SettingHelper.MOUSE_BACK, mouseBack);
            SettingHelper.SetValue(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, favoriteRefreshMinutes);
            SettingHelper.SetValue(SettingHelper.FAVORITE_HIDE_OFFLINE, favoriteHideOffline);
            SettingHelper.SetValue(SettingHelper.NEW_WINDOW_LIVEROOM, newWindowLiveRoom);
            SettingHelper.SetValue(SettingHelper.VIDEO_DECODER, videoDecoder);
            SettingHelper.SetValue(SettingHelper.DOUYU_SIGN_ENABLED, douyuSignEnabled);
            SettingHelper.SetValue(SettingHelper.DOUYU_SIGN_URL, douyuSignUrl ?? "");
            SettingHelper.SetValue(SettingHelper.DOUYIN_SIGN_ENABLED, douyinSignEnabled);
            SettingHelper.SetValue(SettingHelper.DOUYIN_SIGN_URL, douyinSignUrl ?? "");
            SettingHelper.SetValue(SettingHelper.MESSAGE_FONTSIZE, messageFontSize);
            SettingHelper.SetValue(SettingHelper.PLAYER_VOLUME, playerVolume);
            SettingHelper.SetValue(SettingHelper.PLAYER_BRIGHTNESS, playerBrightness);
            SettingHelper.SetValue(SettingHelper.RIGHT_DETAIL_WIDTH, rightDetailWidth);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHOW, liveDanmuShow);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.KEEP_SUPER_CHAT, keepSuperChat);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.DANMU_CLEAN_COUNT, danmuCleanCount);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.TOP_MARGIN, liveDanmuTopMargin);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.AREA, liveDanmuArea);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.FONT_ZOOM, liveDanmuFontZoom);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SPEED, liveDanmuSpeed);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.BOLD, liveDanmuBold);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.COLOURFUL, liveDanmuColourful);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.BORDER_STYLE, liveDanmuBorderStyle);
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.OPACITY, liveDanmuOpacity);
            SettingHelper.SetValue(SettingHelper.LOG_ENABLED, logEnabled);
            SettingHelper.SetValue(SettingHelper.IGNORE_BILI_LOGIN_TIP, ignoreBiliLoginTip);

            if (data.ShieldWords != null)
            {
                ApplyShieldWords(data.ShieldWords);
            }

            if (data.DouyinCookie != null)
            {
                var normalizedCookie = NormalizeDouyinCookieInput(data.DouyinCookie);
                var trimmed = TrimDouyinCookieForSettings(normalizedCookie);
                SettingHelper.SetValue(SettingHelper.DOUYIN_COOKIE, trimmed);
                ApplyDouyinCookieSetting(normalizedCookie);
                await DouyinCookieStore.SaveAsync(normalizedCookie);
                _isUpdatingDouyinCookie = true;
                txtDouyinCookie.Text = normalizedCookie;
                txtDouyinCookie.SelectionStart = txtDouyinCookie.Text.Length;
                _isUpdatingDouyinCookie = false;
            }

            ApplyBiliSettingsBackup(data);
            if (data.Favorites != null)
            {
                ApplyFavoriteBackup(data.Favorites);
            }

            cbTheme.SelectedIndex = theme;
            cbXboxMode.SelectedIndex = xboxMode;
            cbPaneDisplayMode.SelectedIndex = paneDisplayMode;
            swMouseClosePage.IsOn = mouseBack;
            numFavoriteRefreshInterval.Value = favoriteRefreshMinutes;
            swNewWindow.IsOn = newWindowLiveRoom;
            cbDecoder.SelectedIndex = videoDecoder;
            swDouyuSignService.IsOn = douyuSignEnabled;
            txtDouyuSignUrl.Text = douyuSignUrl ?? "";
            swDouyinSignService.IsOn = douyinSignEnabled;
            txtDouyinSignUrl.Text = douyinSignUrl ?? "";
            numFontsize.Value = messageFontSize;
            DanmuSettingState.IsOn = liveDanmuShow;
            SettingKeepSC.IsOn = keepSuperChat;
            numCleanCount.Value = danmuCleanCount;
            swLogEnabled.IsOn = logEnabled;

            ThemeHelper.Apply(theme, Window.Current?.Content as FrameworkElement);
            LogHelper.SetEnabled(logEnabled);
            ApplyDouyuSignServiceSetting(txtDouyuSignUrl.Text, swDouyuSignService.IsOn);
            ApplyDouyinSignServiceSetting(txtDouyinSignUrl.Text, swDouyinSignService.IsOn);
            _ = UpdateDouyuSignStatusAsync();
            _ = UpdateDouyinSignStatusAsync();
        }

        private async void BtnSettingsExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var backup = await BuildSettingsBackupAsync();
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = $"AllLive-Settings-{DateTime.Now:yyyyMMdd-HHmmss}"
                };
                picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }
                var json = JsonConvert.SerializeObject(backup, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
                Utils.ShowMessageToast("设置已备份");
            }
            catch (Exception ex)
            {
                Utils.ShowMessageToast($"备份失败：{ex.Message}");
            }
        }

        private async void BtnSettingsImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }
                var content = await FileIO.ReadTextAsync(file);
                var backup = JsonConvert.DeserializeObject<SettingsBackupData>(content);
                if (backup == null)
                {
                    Utils.ShowMessageToast("设置文件为空");
                    return;
                }
                await ApplySettingsBackupAsync(backup);
                Utils.ShowMessageToast("设置已还原");
            }
            catch (Exception ex)
            {
                Utils.ShowMessageToast($"还原失败：{ex.Message}");
            }
        }

        private static void ApplyDouyuSignServiceSetting(string value, bool enabled)
        {
            var url = string.IsNullOrWhiteSpace(value) ? SettingHelper.DOUYU_SIGN_URL_DEFAULT : value.Trim();
            if (enabled)
            {
                CoreConfig.SetDouyuSignServiceUrl(url);
            }
            else
            {
                CoreConfig.SetDouyuSignServiceUrl(SettingHelper.DOUYU_SIGN_URL_PUBLIC);
            }
        }

        private static void ApplyDouyinSignServiceSetting(string value, bool enabled)
        {
            var url = string.IsNullOrWhiteSpace(value) ? SettingHelper.DOUYIN_SIGN_URL_DEFAULT : value.Trim();
            if (enabled)
            {
                CoreConfig.SetDouyinSignServiceUrl(url);
            }
            else
            {
                CoreConfig.SetDouyinSignServiceUrl(SettingHelper.DOUYIN_SIGN_URL_PUBLIC);
            }
        }

        private static void ApplyDouyinCookieSetting(string value)
        {
            CoreConfig.SetDouyinCookie(value);
        }

        private async Task LoadDouyinCookieAsync()
        {
            var cookie = await DouyinCookieStore.LoadAsync();
            if (string.IsNullOrWhiteSpace(cookie))
            {
                cookie = SettingHelper.GetValue<string>(SettingHelper.DOUYIN_COOKIE, "");
            }
            _isUpdatingDouyinCookie = true;
            txtDouyinCookie.Text = cookie ?? "";
            txtDouyinCookie.SelectionStart = txtDouyinCookie.Text.Length;
            _isUpdatingDouyinCookie = false;
            ApplyDouyinCookieSetting(cookie ?? "");
        }

        private static string NormalizeDouyinCookieInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            var cookie = value.Trim();
            if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            {
                cookie = cookie.Substring("Cookie:".Length).Trim();
            }
            return cookie;
        }

        private static string TrimDouyinCookieForSettings(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            var keepKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ttwid",
                "msToken",
                "__ac_nonce",
                "s_v_web_id"
            };
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                var key = kv[0].Trim();
                if (string.IsNullOrEmpty(key) || !keepKeys.Contains(key))
                {
                    continue;
                }
                var val = kv.Length > 1 ? kv[1].Trim() : "";
                map[key] = val;
            }
            if (map.Count == 0)
            {
                return "";
            }
            var orderedKeys = new[] { "ttwid", "msToken", "__ac_nonce", "s_v_web_id" };
            var items = new List<string>();
            foreach (var key in orderedKeys)
            {
                if (map.TryGetValue(key, out var val))
                {
                    items.Add($"{key}={val}");
                }
            }
            return string.Join("; ", items);
        }

        private async void BtnDouyuSignCheck_Click(object sender, RoutedEventArgs e)
        {
            await UpdateDouyuSignStatusAsync(true);
        }

        private async void BtnDouyinSignCheck_Click(object sender, RoutedEventArgs e)
        {
            await UpdateDouyinSignStatusAsync(true);
        }

        private async Task UpdateDouyuSignStatusAsync(bool showToast = false)
        {
            if (!swDouyuSignService.IsOn)
            {
                txtDouyuSignStatus.Text = "已关闭";
                return;
            }

            var url = string.IsNullOrWhiteSpace(txtDouyuSignUrl.Text)
                ? SettingHelper.DOUYU_SIGN_URL_DEFAULT
                : txtDouyuSignUrl.Text.Trim();
            var healthUrl = BuildHealthUrl(url);
            txtDouyuSignStatus.Text = "检测中...";

            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                using (var response = await client.GetAsync(healthUrl))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        txtDouyuSignStatus.Text = "运行中";
                        if (showToast)
                        {
                            Utils.ShowMessageToast("签名服务运行正常");
                        }
                        return;
                    }
                    txtDouyuSignStatus.Text = $"异常({(int)response.StatusCode})";
                }
            }
            catch (Exception ex)
            {
                txtDouyuSignStatus.Text = "不可用";
                if (showToast)
                {
                    Utils.ShowMessageToast($"签名服务不可用：{ex.Message}");
                }
            }
        }

        private async Task UpdateDouyinSignStatusAsync(bool showToast = false)
        {
            if (!swDouyinSignService.IsOn)
            {
                txtDouyinSignStatus.Text = "已关闭";
                return;
            }

            var url = string.IsNullOrWhiteSpace(txtDouyinSignUrl.Text)
                ? SettingHelper.DOUYIN_SIGN_URL_DEFAULT
                : txtDouyinSignUrl.Text.Trim();
            var healthUrl = BuildHealthUrl(url);
            txtDouyinSignStatus.Text = "检测中...";

            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                using (var response = await client.GetAsync(healthUrl))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        txtDouyinSignStatus.Text = "运行中";
                        if (showToast)
                        {
                            Utils.ShowMessageToast("签名服务运行正常");
                        }
                        return;
                    }
                    txtDouyinSignStatus.Text = $"异常({(int)response.StatusCode})";
                }
            }
            catch (Exception ex)
            {
                txtDouyinSignStatus.Text = "不可用";
                if (showToast)
                {
                    Utils.ShowMessageToast($"签名服务不可用：{ex.Message}");
                }
            }
        }

        private static string BuildHealthUrl(string signUrl)
        {
            if (Uri.TryCreate(signUrl, UriKind.Absolute, out var uri))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = "/health",
                    Query = ""
                };
                return builder.Uri.ToString();
            }
            return signUrl.TrimEnd('/') + "/health";
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SubscribeBiliAccountChanged();
            UpdateBiliAccountUi();
            version.Text = $"{SystemInformation.Instance.ApplicationVersion.Major}.{SystemInformation.Instance.ApplicationVersion.Minor}.{SystemInformation.Instance.ApplicationVersion.Build}";
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

        private async void BtnProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            await Launcher.LaunchFolderPathAsync(@"D:\D-Software\C1og-AllLive");
        }

        private async void BtnLog_Click(object sender, RoutedEventArgs e)
        {
            Windows.Storage.StorageFolder storageFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var logFolder = await storageFolder.CreateFolderAsync("log", Windows.Storage.CreationCollisionOption.OpenIfExists);
            await Launcher.LaunchFolderAsync(logFolder);
        }

        private async void BtnLoginBili_Click(object sender, RoutedEventArgs e)
        {
            if (BiliAccount.Instance.Logined)
            {
                Utils.ShowMessageToast("已登录");
                return;
            }
            var result= await MessageCenter.BiliBiliLogin();
            if (result)
            {
                UpdateBiliAccountUi();
            }
        }

        private void BtnLogoutBili_Click(object sender, RoutedEventArgs e)
        {
            BiliAccount.Instance.Logout();
            UpdateBiliAccountUi();

        }
    }
}
