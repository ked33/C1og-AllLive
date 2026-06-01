using AllLive.UWP.Helper;
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

// https://go.microsoft.com/fwlink/?LinkId=234238 上介绍了“空白页”项模板

namespace AllLive.UWP.Views
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class FavoritePage : Page
    {
        readonly FavoriteVM favoriteVM;
        private DispatcherTimer autoRefreshTimer;
        private int autoRefreshMinutes;
        private bool isPageActive;
        private bool isUpdateFavoriteSubscribed;
        public FavoritePage()
        {
            favoriteVM = new FavoriteVM();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            this.InitializeComponent();
        }

        private void MessageCenter_UpdateFavoriteEvent(object sender, EventArgs e)
        {
            if (!ShouldRunAutoRefresh())
            {
                return;
            }
            favoriteVM.RefreshLiveStatusOnly();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            isPageActive = true;
            SubscribeUpdateFavorite();
            favoriteVM.HideOffline = SettingHelper.GetValue<bool>(SettingHelper.FAVORITE_HIDE_OFFLINE, false);

            if(favoriteVM.Items.Count==0)
            {
                favoriteVM.LoadData();
            }

            StartAutoRefreshTimer();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            isPageActive = false;
            UnsubscribeUpdateFavorite();
            favoriteVM.CancelRefresh();
        }

        private void SubscribeUpdateFavorite()
        {
            if (isUpdateFavoriteSubscribed)
            {
                return;
            }
            MessageCenter.UpdateFavoriteEvent += MessageCenter_UpdateFavoriteEvent;
            isUpdateFavoriteSubscribed = true;
        }

        private void UnsubscribeUpdateFavorite()
        {
            if (!isUpdateFavoriteSubscribed)
            {
                return;
            }
            MessageCenter.UpdateFavoriteEvent -= MessageCenter_UpdateFavoriteEvent;
            isUpdateFavoriteSubscribed = false;
        }

        private void ls_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as FavoriteItem;
            var site = MainVM.Sites.FirstOrDefault(x => x.Name == item.SiteName);
            MessageCenter.OpenLiveRoom(site.LiveSite, new Core.Models.LiveRoomItem()
            {
                RoomID = item.RoomID
            });
        }

        private void MenuFlyoutItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as MenuFlyoutItem).DataContext as FavoriteItem;
            favoriteVM.RemoveItem(item);
        }

        private async void MenuFlyoutItem_Sort_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as MenuFlyoutItem).DataContext as FavoriteItem;
            if (item == null)
            {
                return;
            }
            ContentDialog dialog = new ContentDialog();
            dialog.Title = "排序";
            TextBox textBox = new TextBox();
            textBox.PlaceholderText = "请输入排序数字";
            textBox.Text = item.SortOrder.ToString();
            textBox.InputScope = new InputScope
            {
                Names = { new InputScopeName(InputScopeNameValue.Number) }
            };
            dialog.Content = textBox;
            dialog.PrimaryButtonText = "确定";
            dialog.SecondaryButtonText = "取消";
            dialog.PrimaryButtonClick += (s, a) =>
            {
                a.Cancel = true;
                if (!int.TryParse(textBox.Text, out int sortValue))
                {
                    Utils.ShowMessageToast("请输入有效数字");
                    return;
                }
                dialog.Hide();
                favoriteVM.UpdateSort(item, sortValue);
            };
            await dialog.ShowAsync();
        }

        private void StartAutoRefreshTimer()
        {
            autoRefreshMinutes = GetAutoRefreshMinutes();
            if (autoRefreshTimer == null)
            {
                autoRefreshTimer = new DispatcherTimer();
                autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            }
            autoRefreshTimer.Interval = TimeSpan.FromMinutes(autoRefreshMinutes);
            autoRefreshTimer.Start();
        }

        private void AutoRefreshTimer_Tick(object sender, object e)
        {
            var minutes = GetAutoRefreshMinutes();
            if (minutes != autoRefreshMinutes)
            {
                autoRefreshMinutes = minutes;
                autoRefreshTimer.Interval = TimeSpan.FromMinutes(autoRefreshMinutes);
            }
            if (!ShouldRunAutoRefresh())
            {
                return;
            }
            if (favoriteVM.Loading || favoriteVM.LoaddingLiveStatus)
            {
                return;
            }
            favoriteVM.RefreshLiveStatusOnly();
        }

        private bool ShouldRunAutoRefresh()
        {
            return isPageActive || MessageCenter.HasActiveLiveRoomWindows;
        }

        private static int GetAutoRefreshMinutes()
        {
            var minutes = SettingHelper.GetValue<int>(SettingHelper.FAVORITE_AUTO_REFRESH_MINUTES, 5);
            if (minutes < 1)
            {
                minutes = 5;
            }
            return minutes;
        }
    }
}
