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
using Windows.Storage.Pickers;
using Windows.Storage;
using Newtonsoft.Json;
using Windows.UI.Popups;

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
            await ReloadAsync(deferUiUpdate: false, loadRoomDetail: true);
        }

        public void CancelRefresh()
        {
            Interlocked.Increment(ref _refreshVersion);
            Loading = false;
            LoaddingLiveStatus = false;
        }

        private async Task ReloadAsync(bool deferUiUpdate, bool loadRoomDetail)
        {
            var version = Interlocked.Increment(ref _refreshVersion);
            try
            {
                Loading = true;
                var list = await DatabaseHelper.GetFavorites();
                if (version != _refreshVersion)
                {
                    return;
                }
                if (!loadRoomDetail)
                {
                    PreserveExistingLiveTitles(list);
                    ApplyFavoriteLiveInfo(list, MessageCenter.GetFavoriteLiveInfoCache());
                }
                if (list.Count == 0)
                {
                    ApplySortAndFilter(list);
                    return;
                }
                if (!deferUiUpdate)
                {
                    ApplySortAndFilter(list);
                }
                LoaddingLiveStatus = true;
                await UpdateLiveStatusAsync(list, version, loadRoomDetail);
                if (version != _refreshVersion)
                {
                    return;
                }
                ApplySortAndFilter(list);
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

        private void PreserveExistingLiveTitles(IList<FavoriteItem> items)
        {
            if (items == null || Items == null || Items.Count == 0)
            {
                return;
            }
            var titleMap = Items
                .Where(x => !string.IsNullOrWhiteSpace(x.LiveTitle))
                .GroupBy(GetFavoriteItemKey)
                .ToDictionary(x => x.Key, x => x.First().LiveTitle, StringComparer.OrdinalIgnoreCase);
            if (titleMap.Count == 0)
            {
                return;
            }
            foreach (var item in items)
            {
                if (titleMap.TryGetValue(GetFavoriteItemKey(item), out var title))
                {
                    item.LiveTitle = title;
                }
            }
        }

        private static string GetFavoriteItemKey(FavoriteItem item)
        {
            return $"{item?.SiteName}|{item?.RoomID}";
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

        private async Task UpdateLiveStatusAsync(IList<FavoriteItem> items, int refreshVersion, bool loadRoomDetail)
        {
            var tasks = new List<Task>(items.Count);
            foreach (var item in items)
            {
                tasks.Add(UpdateLiveStatusAsync(item, refreshVersion, loadRoomDetail));
            }
            await Task.WhenAll(tasks);
        }

        private async Task UpdateLiveStatusAsync(FavoriteItem item, int refreshVersion, bool loadRoomDetail)
        {
            if (refreshVersion != _refreshVersion)
            {
                return;
            }
            await _liveStatusSemaphore.WaitAsync();
            try
            {
                if (refreshVersion != _refreshVersion)
                {
                    return;
                }
                var site = MainVM.Sites.FirstOrDefault(x => x.Name == item.SiteName);
                if (site != null)
                {
                    var status = await site.LiveSite.GetLiveStatus(item.RoomID);
                    if (refreshVersion != _refreshVersion)
                    {
                        return;
                    }
                    item.LiveStatus = status;
                    if (!status)
                    {
                        item.LiveTitle = string.Empty;
                        return;
                    }
                    if (!loadRoomDetail)
                    {
                        return;
                    }

                    var detail = await site.LiveSite.GetRoomDetail(item.RoomID);
                    if (refreshVersion != _refreshVersion)
                    {
                        return;
                    }
                    if (detail != null)
                    {
                        item.LiveStatus = detail.Status;
                        item.LiveTitle = detail.Status ? NormalizeLiveTitle(detail.Title) : string.Empty;
                    }
                    else
                    {
                        item.LiveTitle = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"获取直播状态失败:{item.SiteName}-{item.RoomID}", LogType.ERROR, ex);
            }
            finally
            {
                _liveStatusSemaphore.Release();
            }
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
            _ = ReloadAsync(deferUiUpdate: true, loadRoomDetail: true);
        }

        public void RefreshLiveStatusOnly()
        {
            base.Refresh();
            _ = ReloadAsync(deferUiUpdate: true, loadRoomDetail: false);
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

        public void Tip()
        {
            MessageDialog dialog = new MessageDialog(@"该程序兼容Simple Live，您可以导入Simple Live的关注数据，导出的数据也可以在Simple Live中导入。", "导入导出说明");
           _= dialog.ShowAsync();
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
