using AllLive.Core.Interface;
using AllLive.Core.Models;
using AllLive.UWP.Controls;
using AllLive.UWP.Models;
using AllLive.UWP.ViewModels;
using AllLive.UWP.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.Helper
{
    public static class MessageCenter
    {
        private static readonly ConcurrentDictionary<string, int> OpenLiveRoomWindows = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTimeOffset> OpeningLiveRoomWindows = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, FavoriteLiveInfo> FavoriteLiveInfoCache = new ConcurrentDictionary<string, FavoriteLiveInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan LiveRoomWindowOpenThrottle = TimeSpan.FromSeconds(3);
        private static int ActiveLiveRoomWindowCount;
        private static int LiveRoomMemoryCleanupScheduled;
        public delegate void NavigatePageHandler(Type page, object data);
        public static event NavigatePageHandler NavigatePageEvent;
        public delegate void ChangeTitleHandler(string title, string logo);
        public static event ChangeTitleHandler ChangeTitleEvent;
        public static event EventHandler<bool> HideTitlebarEvent;
        public static event EventHandler UpdateFavoriteEvent;
        public static event EventHandler<FavoriteLiveInfo> FavoriteLiveInfoUpdatedEvent;
        public static event EventHandler UpdatePanelDisplayModeEvent;
        public static bool HasActiveLiveRoomWindows => Volatile.Read(ref ActiveLiveRoomWindowCount) > 0;
        public async static void OpenLiveRoom(ILiveSite liveSite, LiveRoomItem item)
        {
            var arg = new PageArgs()
            {
                Site = liveSite,
                Data = item
            };

            // 如果是哔哩哔哩
            if (liveSite.Name == "哔哩哔哩直播" && !BiliAccount.Instance.Logined&&!SettingHelper.GetValue(SettingHelper.IGNORE_BILI_LOGIN_TIP,false))
            {
                // 弹窗询问是否登录
                MessageDialog dialog = new MessageDialog("您尚未登录哔哩哔哩账号，部分直播可能无法观看，是否前往登录账号？", "未登录");
                dialog.Commands.Add(new UICommand("登录", async (cmd) =>
                {
                    // 调用登录方法
                    var login = await BiliBiliLogin();
                    if (login)
                    {
                        // 登录成功后打开直播间
                        NavigatePage(typeof(LiveRoomPage), arg);
                    }
                    else
                    {
                        Utils.ShowMessageToast("未登录成功");
                        NavigatePage(typeof(LiveRoomPage), arg);
                    }
                }));
                dialog.Commands.Add(new UICommand("取消", (cmd) =>
                 {
                   NavigatePage(typeof(LiveRoomPage), arg);
                }));
                dialog.Commands.Add(new UICommand("不再提示", (cmd) =>
                {
                    SettingHelper.SetValue(SettingHelper.IGNORE_BILI_LOGIN_TIP, true);
                    NavigatePage(typeof(LiveRoomPage), arg);
                }));
                await dialog.ShowAsync();
                return;
            }

            //if (SettingHelper.GetValue(SettingHelper.NEW_WINDOW_LIVEROOM, false))
            //{
            //    CoreApplicationView newView = CoreApplication.CreateNewView();
            //    int newViewId = 0;
            //    await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            //    {
            //        Frame frame = new Frame();
            //        frame.Navigate(typeof(LiveRoomPage), arg);
            //        Window.Current.Content = frame;
            //        Window.Current.Activate();
            //        newViewId = ApplicationView.GetForCurrentView().Id;
            //        ApplicationView.GetForCurrentView().Consolidated += (sender, args) =>
            //        {
            //            frame.Navigate(typeof(BlankPage));
            //            CoreWindow.GetForCurrentThread().Close();
            //        };
            //    });
            //    bool viewShown = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(newViewId);
            //}
            //else
            //{
                NavigatePage(typeof(LiveRoomPage), arg);
                //(Window.Current.Content as Frame).Navigate(typeof(LiveRoomPage), arg);
           // }

        }

        public async static void NavigatePage(Type page, object data)
        {
            if(SettingHelper.GetValue(SettingHelper.NEW_WINDOW_LIVEROOM, true)&& page == typeof(LiveRoomPage))
            {
                var liveRoomKey = GetLiveRoomWindowKey(data);
                if (!string.IsNullOrWhiteSpace(liveRoomKey)
                    && OpenLiveRoomWindows.TryGetValue(liveRoomKey, out var existingViewId))
                {
                    var currentViewId = ApplicationView.GetForCurrentView().Id;
                    if (existingViewId == currentViewId)
                    {
                        return;
                    }
                    var switched = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(existingViewId);
                    if (switched)
                    {
                        return;
                    }
                    if (IsLiveRoomWindowOpening(liveRoomKey))
                    {
                        return;
                    }
                    OpenLiveRoomWindows.TryRemove(liveRoomKey, out _);
                }

                if (!TryMarkLiveRoomWindowOpening(liveRoomKey))
                {
                    return;
                }

                try
                {
                    CoreApplicationView newView = CoreApplication.CreateNewView();
                    int newViewId = 0;
                    await newView.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        Frame frame = new Frame();
                        ThemeHelper.Apply(frame);
                        frame.Navigate(typeof(LiveRoomPage), data);
                        Window.Current.Content = frame;
                        Window.Current.Activate();
                        newViewId = ApplicationView.GetForCurrentView().Id;
                        if (!string.IsNullOrWhiteSpace(liveRoomKey))
                        {
                            OpenLiveRoomWindows[liveRoomKey] = newViewId;
                            ApplicationView.GetForCurrentView().Consolidated += (sender, args) =>
                            {
                                if (OpenLiveRoomWindows.TryGetValue(liveRoomKey, out var viewId) && viewId == newViewId)
                                {
                                    OpenLiveRoomWindows.TryRemove(liveRoomKey, out _);
                                }
                            };
                        }
                        //ApplicationView.GetForCurrentView().Consolidated += (sender, args) =>
                        //{
                        //    frame.Navigate(typeof(BlankPage));
                        //    //newView.CoreWindow.Close();
                        //};
                    });
                    bool viewShown = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(newViewId);
                    if (!viewShown && !string.IsNullOrWhiteSpace(liveRoomKey))
                    {
                        OpenLiveRoomWindows.TryRemove(liveRoomKey, out _);
                    }
                }
                finally
                {
                    ClearLiveRoomWindowOpening(liveRoomKey);
                }
            }
            else
            {
                NavigatePageEvent?.Invoke(page, data);
            }
            
        }

        

        public static void ChangeTitle(string title, ILiveSite site = null)
        {
            var logo = "ms-appx:///Assets/Square44x44Logo.png";
            if (site != null)
            {
                var siteInfo = MainVM.Sites.FirstOrDefault(x => x.LiveSite.Equals(site));
                if (siteInfo != null)
                {
                    logo = siteInfo.Logo;
                }
            }

            ChangeTitleEvent?.Invoke(title, logo);
        }
        public static void HideTitlebar(bool show)
        {
            HideTitlebarEvent?.Invoke(null, show);
        }

        public static void UpdateFavorite()
        {
            var dispatcher = CoreApplication.MainView?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    UpdateFavoriteEvent?.Invoke(null, EventArgs.Empty);
                });
                return;
            }
            UpdateFavoriteEvent?.Invoke(null, EventArgs.Empty);
        }
        public static void UpdateFavoriteLiveInfo(string siteName, string roomId, string sourceRoomId, string title, bool liveStatus)
        {
            if (string.IsNullOrWhiteSpace(siteName) || (string.IsNullOrWhiteSpace(roomId) && string.IsNullOrWhiteSpace(sourceRoomId)))
            {
                return;
            }
            var liveInfo = new FavoriteLiveInfo()
            {
                SiteName = siteName,
                RoomID = roomId ?? string.Empty,
                SourceRoomID = sourceRoomId ?? string.Empty,
                Title = title ?? string.Empty,
                LiveStatus = liveStatus
            };
            CacheFavoriteLiveInfo(liveInfo, liveInfo.RoomID);
            CacheFavoriteLiveInfo(liveInfo, liveInfo.SourceRoomID);

            var dispatcher = CoreApplication.MainView?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                _ = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    FavoriteLiveInfoUpdatedEvent?.Invoke(null, liveInfo);
                });
                return;
            }
            FavoriteLiveInfoUpdatedEvent?.Invoke(null, liveInfo);
        }

        public static List<FavoriteLiveInfo> GetFavoriteLiveInfoCache()
        {
            return FavoriteLiveInfoCache.Values.Distinct().ToList();
        }

        private static void CacheFavoriteLiveInfo(FavoriteLiveInfo liveInfo, string roomId)
        {
            if (liveInfo == null || string.IsNullOrWhiteSpace(roomId))
            {
                return;
            }
            FavoriteLiveInfoCache[GetFavoriteLiveInfoKey(liveInfo.SiteName, roomId)] = liveInfo;
        }

        private static string GetFavoriteLiveInfoKey(string siteName, string roomId)
        {
            return $"{siteName}|{roomId}";
        }
        public static void UpdatePanelDisplayMode()
        {
            UpdatePanelDisplayModeEvent?.Invoke(null, new EventArgs());
        }

        public static void RegisterLiveRoomWindow()
        {
            Interlocked.Increment(ref ActiveLiveRoomWindowCount);
        }

        public static void UnregisterLiveRoomWindow()
        {
            var count = Interlocked.Decrement(ref ActiveLiveRoomWindowCount);
            if (count <= 0)
            {
                Interlocked.Exchange(ref ActiveLiveRoomWindowCount, 0);
                ScheduleLastLiveRoomMemoryCleanup();
            }
        }

        private static void ScheduleLastLiveRoomMemoryCleanup()
        {
            if (Interlocked.CompareExchange(ref LiveRoomMemoryCleanupScheduled, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    if (Volatile.Read(ref ActiveLiveRoomWindowCount) > 0)
                    {
                        return;
                    }

                    LogMemorySnapshot("最后一个直播间已关闭，开始回收内存");

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    LogMemorySnapshot("直播间内存回收完成");
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    if (Volatile.Read(ref ActiveLiveRoomWindowCount) == 0)
                    {
                        LogMemorySnapshot("直播间内存回收后10秒采样");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    if (Volatile.Read(ref ActiveLiveRoomWindowCount) == 0)
                    {
                        LogMemorySnapshot("直播间内存回收后30秒采样");
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Log("直播间内存回收失败", LogType.ERROR, ex);
                }
                finally
                {
                    Interlocked.Exchange(ref LiveRoomMemoryCleanupScheduled, 0);
                }
            });
        }

        private static void LogMemorySnapshot(string stage)
        {
            if (!LogHelper.Enabled)
            {
                return;
            }

            LogHelper.Log($"{stage}。AppMemory={MemoryManager.AppMemoryUsage} Managed={GC.GetTotalMemory(false)} ActiveLiveRoomWindows={Volatile.Read(ref ActiveLiveRoomWindowCount)}", LogType.DEBUG);
        }

        public static async Task<bool> BiliBiliLogin()
        {
            BiliLoginDialog biliLoginDialog = new BiliLoginDialog();
            await biliLoginDialog.ShowAsync();
            return BiliAccount.Instance.Logined;

        }

        private static string GetLiveRoomWindowKey(object data)
        {
            var pageArgs = data as PageArgs;
            var room = pageArgs?.Data as LiveRoomItem;
            var siteName = pageArgs?.Site?.Name?.Trim();
            var roomId = room?.RoomID?.Trim();
            if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(roomId))
            {
                return null;
            }
            return $"{siteName}|{roomId}";
        }

        private static bool TryMarkLiveRoomWindowOpening(string liveRoomKey)
        {
            if (string.IsNullOrWhiteSpace(liveRoomKey))
            {
                return true;
            }

            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                if (!OpeningLiveRoomWindows.TryGetValue(liveRoomKey, out var openingAt))
                {
                    return OpeningLiveRoomWindows.TryAdd(liveRoomKey, now);
                }
                if ((now - openingAt) < LiveRoomWindowOpenThrottle)
                {
                    return false;
                }
                OpeningLiveRoomWindows.TryRemove(liveRoomKey, out _);
            }
        }

        private static bool IsLiveRoomWindowOpening(string liveRoomKey)
        {
            if (string.IsNullOrWhiteSpace(liveRoomKey))
            {
                return false;
            }

            if (!OpeningLiveRoomWindows.TryGetValue(liveRoomKey, out var openingAt))
            {
                return false;
            }

            var isOpening = (DateTimeOffset.UtcNow - openingAt) < LiveRoomWindowOpenThrottle;
            if (!isOpening)
            {
                OpeningLiveRoomWindows.TryRemove(liveRoomKey, out _);
            }
            return isOpening;
        }

        private static void ClearLiveRoomWindowOpening(string liveRoomKey)
        {
            if (string.IsNullOrWhiteSpace(liveRoomKey))
            {
                return;
            }
            OpeningLiveRoomWindows.TryRemove(liveRoomKey, out _);
        }
    }
    public class FavoriteLiveInfo
    {
        public string SiteName { get; set; }
        public string RoomID { get; set; }
        public string SourceRoomID { get; set; }
        public string Title { get; set; }
        public bool LiveStatus { get; set; }
    }
    class BlankPage : Page { }

}
