using AllLive.Core.Interface;
using AllLive.UWP.Helper;
using AllLive.UWP.Models;
using AllLive.UWP.ViewModels;
using AllLive.UWP.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
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
using Windows.ApplicationModel.Core;
using System.Threading.Tasks;
using Windows.Services.Store;
using Windows.UI.Popups;

// https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x804 上介绍了“空白页”项模板

namespace AllLive.UWP
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的空白页。
    /// </summary>
    public sealed partial class MainPage : Page
    {

        public MainPage()
        {

            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            this.InitializeComponent();
            MessageCenter.UpdatePanelDisplayModeEvent += MessageCenter_UpdatePanelDisplayModeEvent;
            this.KeyDown += MainPage_KeyDown;
            SetPaneMode();
        }

        private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.GamepadMenu)
            {
                e.Handled = true;
                // 切换设置

                navigationView.SelectedItem = navigationView.SettingsItem;
            }
            else if (e.Key == Windows.System.VirtualKey.GamepadY)
            {
                e.Handled = true;
                searchBox.Focus(FocusState.Programmatic);
            }
        }

        private void MessageCenter_UpdatePanelDisplayModeEvent(object sender, EventArgs e)
        {
            SetPaneMode();
        }

        private void SetPaneMode()
        {
            if (Utils.IsXbox)
            {
                navigationView.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Top;
                MessageCenter.HideTitlebar(true);
                return;
            }
            if (SettingHelper.GetValue<int>(SettingHelper.PANE_DISPLAY_MODE, 0) == 0)
            {
                navigationView.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Left;
            }
            else
            {
                navigationView.PaneDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Top;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = BiliAccount.Instance.InitLoginInfo();
            _ = CheckUpdate();
        }

        private void NavigationView_SelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
        {
            var item = args.SelectedItem as Microsoft.UI.Xaml.Controls.NavigationViewItem;
            if (item.Tag.ToString() == "设置" || item.Tag.ToString() == "Settings")
            {
                item.Tag = "SettingsPage";
            }
            frame.Navigate(Type.GetType("AllLive.UWP.Views." + item.Tag));

        }

        private async void searchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.QueryText))
            {
                Helper.Utils.ShowMessageToast("关键字不能为空");
                return;
            }
            if (!await ParseUrl(args.QueryText))
            {
                this.Frame.Navigate(typeof(SearchPage), args.QueryText);
            }
        }

        private async Task<bool> ParseUrl(string url)
        {
            var parseResult = await SiteParser.ParseUrl(url);
            if (parseResult.Item1 != LiveSite.Unknown && !string.IsNullOrEmpty(parseResult.Item2))
            {
                MessageCenter.OpenLiveRoom(MainVM.Sites[(int)parseResult.Item1].LiveSite, new LiveRoomItem()
                {
                    RoomID = parseResult.Item2,
                });
                return true;
            }
            else
            {
                return false;
            }


        }

        private void navigationView_Loaded(object sender, RoutedEventArgs e)
        {
            navigationView.IsPaneOpen = false;
        }

        private async Task CheckUpdate()
        {
            try
            {
                StoreContext context = StoreContext.GetDefault();
                IReadOnlyList<StorePackageUpdate> updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();

                if (updates.Count > 0)
                {
                    var dialog = ThemeHelper.CreateContentDialog();
                    dialog.Title = "发现新版本";
                    dialog.Content = new TextBlock
                    {
                        Text = "发现新版本，是否前往应用商店更新？",
                        TextWrapping = TextWrapping.Wrap
                    };
                    dialog.PrimaryButtonText = "确定";
                    dialog.SecondaryButtonText = "取消";
                    dialog.DefaultButton = ContentDialogButton.Primary;
                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        var product = await context.GetStoreProductForCurrentAppAsync();
                        var uri = new Uri($"ms-windows-store://pdp?productid={product.Product.StoreId}");
                        await Windows.System.Launcher.LaunchUriAsync(uri);
                    }

                }

            }
            catch (Exception ex)
            {
                LogHelper.Log("CheckUpdate", LogType.ERROR, ex);
                await Helper.Utils.CheckVersion();
            }


        }
    }
}
