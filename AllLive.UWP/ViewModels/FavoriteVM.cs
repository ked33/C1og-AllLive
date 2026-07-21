using AllLive.UWP.Helper;
using AllLive.UWP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Windows.ApplicationModel.Core;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.UI.Core;
using Newtonsoft.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.ViewModels
{
    public class FavoriteVM : BaseViewModel
    {
        public FavoriteVM()
        {
            Items = new ObservableCollection<FavoriteItem>();
            DisplayItems = new ObservableCollection<FavoriteItem>();
            _hideOffline = SettingHelper.GetValue<bool>(SettingHelper.FAVORITE_HIDE_OFFLINE, false);
            InputCommand = new RelayCommand(Input);
            OutputCommand = new RelayCommand(Output);
            TipCommand = new RelayCommand(Tip);
        }

        public ICommand InputCommand { get; set; }
        public ICommand OutputCommand { get; set; }
        public ICommand TipCommand { get; set; }


        private ObservableCollection<FavoriteItem> _items;
        public ObservableCollection<FavoriteItem> Items
        {
            get { return _items; }
            set { _items = value; DoPropertyChanged("Items"); }
        }

        private ObservableCollection<FavoriteItem> _displayItems;
        public ObservableCollection<FavoriteItem> DisplayItems
        {
            get { return _displayItems; }
            set { _displayItems = value; DoPropertyChanged("DisplayItems"); }
        }

        private bool _hideOffline = false;
        public bool HideOffline
        {
            get { return _hideOffline; }
            set
            {
                if (_hideOffline == value)
                {
                    return;
                }
                _hideOffline = value;
                SettingHelper.SetValue(SettingHelper.FAVORITE_HIDE_OFFLINE, value);
                DoPropertyChanged("HideOffline");
                ApplySortAndFilter();
            }
        }

        private bool _loadingLiveStatus;

        public bool LoaddingLiveStatus
        {
            get { return _loadingLiveStatus; }
            set { _loadingLiveStatus = value; DoPropertyChanged("LoaddingLiveStatus"); }
        }

        private int _refreshVersion = 0;
        private const int LiveStatusParallelism = 6;
        private readonly SemaphoreSlim _liveStatusSemaphore = new SemaphoreSlim(LiveStatusParallelism, LiveStatusParallelism);

        public async void LoadData()
        {
            await ReloadAsync(loadRoomDetail: true);
        }

        public void CancelRefresh()
        {
            Interlocked.Increment(ref _refreshVersion);
            Loading = false;
            LoaddingLiveStatus = false;
        }

        private async Task ReloadAsync(bool loadRoomDetail)
        {
            var version = Interlocked.Increment(ref _refreshVersion);
            // 保留上一轮开播状态/标题，避免刷新时整表闪成「未开播」再统一蹦出。
            var previousTitleStates = CaptureFavoriteTitleSnapshots();
            try
            {
                Loading = true;
                var list = await DatabaseHelper.GetFavorites();
                if (version != _refreshVersion)
                {
                    return;
                }
                RestorePreviousLiveInfo(list, previousTitleStates);
                // 读库后立刻上屏；状态检测完成的项再增量更新 DisplayItems。
                ApplySortAndFilter(list);
                if (list.Count == 0)
                {
                    return;
                }
                LoaddingLiveStatus = true;
                var failedCount = await UpdateLiveStatusAsync(list, version, loadRoomDetail);
                if (version != _refreshVersion)
                {
                    return;
                }
                // 全部结束后再整表排序/过滤一次，修正增量阶段的顺序。
                await RunOnUiThreadAsync(() => ApplySortAndFilter(list));
                if (failedCount > 0)
                {
                    Utils.ShowMessageToast(failedCount >= list.Count
                        ? "开播状态获取失败，请检查网络连接"
                        : $"{failedCount} 个直播间的开播状态获取失败");
                }
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
            finally
            {
                if (version == _refreshVersion)
                {
                    Loading = false;
                    LoaddingLiveStatus = false;
                }
            }
        }

        private Dictionary<string, FavoriteTitleSnapshot> CaptureFavoriteTitleSnapshots()
        {
            if (Items == null || Items.Count == 0)
            {
                return new Dictionary<string, FavoriteTitleSnapshot>(StringComparer.OrdinalIgnoreCase);
            }

            return Items
                .Where(x => x != null)
                .GroupBy(GetFavoriteItemKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    x => x.Key,
                    x =>
                    {
                        var item = x.First();
                        return new FavoriteTitleSnapshot()
                        {
                            LiveStatus = item.LiveStatus,
                            LiveTitle = item.LiveTitle ?? string.Empty
                        };
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        private static void RestorePreviousLiveInfo(IList<FavoriteItem> items, IDictionary<string, FavoriteTitleSnapshot> previousTitleStates)
        {
            if (items == null || previousTitleStates == null || previousTitleStates.Count == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }
                if (!previousTitleStates.TryGetValue(GetFavoriteItemKey(item), out var previous))
                {
                    continue;
                }
                item.LiveStatus = previous.LiveStatus;
                item.LiveTitle = previous.LiveStatus
                    ? NormalizeLiveTitle(previous.LiveTitle)
                    : string.Empty;
            }
        }

        private static string GetFavoriteItemKey(FavoriteItem item)
        {
            return $"{item?.SiteName}|{item?.RoomID}";
        }

        private class FavoriteTitleSnapshot
        {
            public bool LiveStatus { get; set; }
            public string LiveTitle { get; set; }
        }

        public void ApplyFavoriteLiveInfo(FavoriteLiveInfo liveInfo)
        {
            if (ApplyFavoriteLiveInfo(Items, liveInfo))
            {
                ApplySortAndFilter();
            }
        }

        public void ApplyCachedFavoriteLiveInfo()
        {
            if (ApplyFavoriteLiveInfo(Items, MessageCenter.GetFavoriteLiveInfoCache()))
            {
                ApplySortAndFilter();
            }
        }

        private static bool ApplyFavoriteLiveInfo(IList<FavoriteItem> items, IEnumerable<FavoriteLiveInfo> liveInfos)
        {
            if (items == null || liveInfos == null)
            {
                return false;
            }
            var changed = false;
            foreach (var liveInfo in liveInfos)
            {
                changed = ApplyFavoriteLiveInfo(items, liveInfo) || changed;
            }
            return changed;
        }

        private static bool ApplyFavoriteLiveInfo(IList<FavoriteItem> items, FavoriteLiveInfo liveInfo)
        {
            if (items == null || liveInfo == null)
            {
                return false;
            }
            var changed = false;
            foreach (var item in items)
            {
                if (!IsSameFavorite(item, liveInfo))
                {
                    continue;
                }
                if (item.LiveStatus != liveInfo.LiveStatus)
                {
                    item.LiveStatus = liveInfo.LiveStatus;
                    changed = true;
                }
                var title = liveInfo.LiveStatus ? NormalizeLiveTitle(liveInfo.Title) : string.Empty;
                if ((!liveInfo.LiveStatus || !string.IsNullOrWhiteSpace(title)) && item.LiveTitle != title)
                {
                    item.LiveTitle = title;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool IsSameFavorite(FavoriteItem item, FavoriteLiveInfo liveInfo)
        {
            if (item == null || liveInfo == null)
            {
                return false;
            }
            return string.Equals(item.SiteName, liveInfo.SiteName, StringComparison.OrdinalIgnoreCase)
                && (IsSameRoomID(item.RoomID, liveInfo.RoomID)
                    || IsSameRoomID(item.RoomID, liveInfo.SourceRoomID));
        }

        private static bool IsSameRoomID(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first)
                && !string.IsNullOrWhiteSpace(second)
                && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

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

        // 返回 true 表示该关注项的开播状态获取失败（用于在刷新结束后统一提示）。
        // 仅 GetLiveStatus 失败才算开播状态获取失败；已拿到开播状态、仅房间详情失败不计入。
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
                if (site == null)
                {
                    return false;
                }

                bool status;
                try
                {
                    status = await site.LiveSite.GetLiveStatus(item.RoomID);
                }
                catch (Exception ex)
                {
                    LogHelper.Log($"获取直播状态失败:{item.SiteName}-{item.RoomID}", LogType.ERROR, ex);
                    return refreshVersion == _refreshVersion;
                }
                if (refreshVersion != _refreshVersion)
                {
                    return false;
                }

                if (!status)
                {
                    await ApplyItemLiveInfoAsync(item, refreshVersion, liveStatus: false, liveTitle: string.Empty);
                    return false;
                }

                // 先按开播态增量上屏，标题可随后补全，避免等详情拖慢首屏。
                await ApplyItemLiveInfoAsync(item, refreshVersion, liveStatus: true, liveTitle: null);
                if (!loadRoomDetail)
                {
                    return false;
                }

                try
                {
                    var detail = await site.LiveSite.GetRoomDetail(item.RoomID);
                    if (refreshVersion != _refreshVersion)
                    {
                        return false;
                    }
                    if (detail != null)
                    {
                        await ApplyItemLiveInfoAsync(
                            item,
                            refreshVersion,
                            liveStatus: detail.Status,
                            liveTitle: detail.Status ? NormalizeLiveTitle(detail.Title) : string.Empty);
                    }
                    else
                    {
                        await ApplyItemLiveInfoAsync(item, refreshVersion, liveStatus: true, liveTitle: string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    // 开播状态已获取成功，仅房间详情失败，标题留空，不计入失败提示。
                    LogHelper.Log($"获取直播间详情失败:{item.SiteName}-{item.RoomID}", LogType.ERROR, ex);
                    if (refreshVersion == _refreshVersion)
                    {
                        await ApplyItemLiveInfoAsync(item, refreshVersion, liveStatus: true, liveTitle: string.Empty);
                    }
                }
                return false;
            }
            finally
            {
                _liveStatusSemaphore.Release();
            }
        }

        // liveTitle == null 表示不改标题（仅更新开播状态）。
        private async Task ApplyItemLiveInfoAsync(FavoriteItem item, int refreshVersion, bool liveStatus, string liveTitle)
        {
            if (item == null || refreshVersion != _refreshVersion)
            {
                return;
            }
            await RunOnUiThreadAsync(() =>
            {
                if (refreshVersion != _refreshVersion)
                {
                    return;
                }
                item.LiveStatus = liveStatus;
                if (liveTitle != null)
                {
                    item.LiveTitle = liveTitle;
                }
                else if (!liveStatus)
                {
                    item.LiveTitle = string.Empty;
                }
                ProgressiveApplyItem(item);
            });
        }

        // 单条检测完成后增量更新展示：隐藏未开播时即时露出/移除；显示全部时依赖属性通知，整表重排放在刷新结束。
        private void ProgressiveApplyItem(FavoriteItem item)
        {
            if (item == null || DisplayItems == null)
            {
                return;
            }

            if (!HideOffline)
            {
                IsEmpty = DisplayItems.Count == 0;
                return;
            }

            var index = IndexOfDisplayItem(item);
            if (item.LiveStatus)
            {
                if (index < 0)
                {
                    InsertDisplayItemSorted(item);
                }
            }
            else if (index >= 0)
            {
                DisplayItems.RemoveAt(index);
            }
            IsEmpty = DisplayItems.Count == 0;
        }

        private int IndexOfDisplayItem(FavoriteItem item)
        {
            if (item == null || DisplayItems == null)
            {
                return -1;
            }
            for (var i = 0; i < DisplayItems.Count; i++)
            {
                var current = DisplayItems[i];
                if (ReferenceEquals(current, item)
                    || string.Equals(GetFavoriteItemKey(current), GetFavoriteItemKey(item), StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        private void InsertDisplayItemSorted(FavoriteItem item)
        {
            var insertAt = DisplayItems.Count;
            for (var i = 0; i < DisplayItems.Count; i++)
            {
                if (CompareFavoriteDisplayOrder(item, DisplayItems[i]) < 0)
                {
                    insertAt = i;
                    break;
                }
            }
            DisplayItems.Insert(insertAt, item);
        }

        // <0 表示 a 应排在 b 之前（SortOrder 降序，LiveStatus 降序）。
        private static int CompareFavoriteDisplayOrder(FavoriteItem a, FavoriteItem b)
        {
            var sortCmp = b.SortOrder.CompareTo(a.SortOrder);
            if (sortCmp != 0)
            {
                return sortCmp;
            }
            return b.LiveStatus.CompareTo(a.LiveStatus);
        }

        private static async Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return;
            }
            CoreDispatcher dispatcher = null;
            try
            {
                dispatcher = CoreApplication.MainView?.Dispatcher;
            }
            catch
            {
                // MainView 在部分生命周期不可用时回退到当前线程执行。
            }
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
                return;
            }
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }

        private static string NormalizeLiveTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }
            return title.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private void ApplySortAndFilter(IList<FavoriteItem> source = null)
        {
            var baseList = source ?? Items?.ToList() ?? new List<FavoriteItem>();
            if (source != null)
            {
                Items = new ObservableCollection<FavoriteItem>(baseList);
            }
            var list = baseList
                .OrderByDescending(x => x.SortOrder)
                .ThenByDescending(x => x.LiveStatus)
                .ToList();
            if (HideOffline)
            {
                list = list.Where(x => x.LiveStatus).ToList();
            }
            DisplayItems = new ObservableCollection<FavoriteItem>(list);
            IsEmpty = DisplayItems.Count == 0;
        }

        public override void Refresh()
        {
            base.Refresh();
            _ = ReloadAsync(loadRoomDetail: true);
        }

        public void RefreshLiveStatusOnly()
        {
            base.Refresh();
            _ = ReloadAsync(loadRoomDetail: false);
        }

        public void RemoveItem(FavoriteItem item)
        {
            try
            {
                DatabaseHelper.DeleteFavorite(item.ID);
                Items.Remove(item);
                ApplySortAndFilter();
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }

        }

        public async void Input()
        {
          
            // 打开文件选择器
            FileOpenPicker picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.ViewMode = PickerViewMode.List;
            picker.CommitButtonText = "导入";

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    var json = await FileIO.ReadTextAsync(file);
                    var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<FavoriteJsonItem>>(json);
                    foreach (var item in items)
                    {
                        var favoriteItem = new FavoriteItem()
                        {
                            SiteName = item.SiteName,
                            RoomID = item.RoomId,
                            UserName = item.UserName,
                            Photo = item.Face,
                            SortOrder = item.Sort
                        };
                        var existId = DatabaseHelper.CheckFavorite(favoriteItem.RoomID, favoriteItem.SiteName);
                        if (existId != null)
                        {
                            DatabaseHelper.UpdateFavoriteSort(existId.Value, favoriteItem.SortOrder);
                            continue;
                        }
                        DatabaseHelper.AddFavorite(favoriteItem);
                    }
                    Utils.ShowMessageToast("导入成功");
                    RefreshLiveStatusOnly();
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Utils.ShowMessageToast("导入失败");
                }
            }
        }

        public async void Output()
        {
            // 打开文件选择器
            FileSavePicker picker = new FileSavePicker();
            picker.FileTypeChoices.Add("Json", new List<string>() { ".json" });
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            picker.SuggestedFileName = "favorite.json";

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                try
                {
                    var items = new List<FavoriteJsonItem>();
                    foreach (var item in Items)
                    {
                        var siteId = "";
                        switch(item.SiteName)
                        {
                            case "哔哩哔哩直播":
                                siteId = "bilibili";
                                break;
                            case "斗鱼直播":
                                siteId = "douyu";
                                break;
                            case "虎牙直播":
                                siteId = "huya";
                                break;
                            case "抖音直播":
                                siteId = "douyin";
                                break;
                        }

                        items.Add(new FavoriteJsonItem()
                        {
                            SiteId = siteId,
                            Id = $"{siteId}_{item.RoomID}",
                            RoomId = item.RoomID,
                            UserName = item.UserName,
                            Face = item.Photo,
                            AddTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.M"),
                            Sort = item.SortOrder
                        });
                    }
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
                    await FileIO.WriteTextAsync(file, json);
                    Utils.ShowMessageToast("导出成功");
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    Utils.ShowMessageToast("导出失败");
                }
            }


        }

        public async void Tip()
        {
            var dialog = ThemeHelper.CreateContentDialog();
            dialog.Title = "导入导出说明";
            dialog.Content = new TextBlock
            {
                Text = "该程序兼容Simple Live，您可以导入Simple Live的关注数据，导出的数据也可以在Simple Live中导入。",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            dialog.PrimaryButtonText = "知道了";
            dialog.DefaultButton = ContentDialogButton.Primary;
            try
            {
                await dialog.ShowAsync();
            }
            catch
            {
            }
        }

        public void UpdateSort(FavoriteItem item, int sortOrder)
        {
            try
            {
                item.SortOrder = sortOrder;
                DatabaseHelper.UpdateFavoriteSort(item.ID, sortOrder);
                ApplySortAndFilter();
            }
            catch (Exception ex)
            {
                HandleError(ex);
            }
        }
    }

    public class FavoriteJsonItem
    {
        [JsonProperty("siteId")]
        public string SiteId;

        [JsonProperty("id")]
        public string Id;

        [JsonProperty("roomId")]
        public string RoomId;

        [JsonProperty("userName")]
        public string UserName;

        [JsonProperty("face")]
        public string Face;

        [JsonProperty("addTime")]
        public string AddTime;

        [JsonProperty("sort")]
        public int Sort;

        [JsonIgnore]
        public string SiteName
        {
            get
            {
                switch (SiteId)
                {
                    case "bilibili":
                        return "哔哩哔哩直播";
                    case "douyu":
                        return "斗鱼直播";
                    case "huya":
                        return "虎牙直播";
                    case "douyin":
                        return "抖音直播";
                    default:
                        return "未知";
                }
            }
        }
        
    }
}
