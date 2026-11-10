using AllLive.UWP.Controls;
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
    public sealed partial class SearchPage : Page
    {
        readonly SearchVM searchVM;
        public SearchPage()
        {
            searchVM = new SearchVM();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;
            this.InitializeComponent();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MessageCenter.ChangeTitle("直播间搜索");
            if (e.NavigationMode == NavigationMode.New)
            {
                if (e.Parameter != null)
                {
                    searchBox.Text = e.Parameter.ToString();
                    foreach (var item in searchVM.Items) item.SetQuery(searchBox.Text);
                }
            }
            var selected = pivot.SelectedItem as SearchItemVM;
            if (selected != null && !selected.Loading && selected.Items.Count == 0 && !string.IsNullOrWhiteSpace(selected.Keyword))
            {
                selected.LoadData(selected.Keyword);
            }
        }


        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            foreach (var item in searchVM.Items) item.CancelLoad();
            if (e.NavigationMode == NavigationMode.Back)
            {
                this.NavigationCacheMode = NavigationCacheMode.Disabled;
            }
            base.OnNavigatedFrom(e);
        }
        private void btnBack_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame.CanGoBack)
            {
                this.Frame.GoBack();
            }
        }

        private void MyAdaptiveGridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as AllLive.Core.Models.LiveRoomItem;
            var vm = (sender as MyAdaptiveGridView).DataContext as SearchItemVM;
            MessageCenter.OpenLiveRoom(vm.site.LiveSite, item);
        }

        private void searchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (string.IsNullOrEmpty(searchBox.Text))
            {
                Utils.ShowMessageToast("关键字不能为空");
                return;
            }
            foreach (SearchItemVM item in pivot.Items)
            {
                item.SetQuery(searchBox.Text);
            }
            (pivot.SelectedItem as SearchItemVM).LoadData(searchBox.Text);
        }

        private void pivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var vm = pivot.SelectedItem as SearchItemVM;
            if (vm == null || string.IsNullOrWhiteSpace(vm.Keyword)) return;
            if (vm.Loading == false && vm.Items.Count == 0)
            {
                vm.LoadData(vm.Keyword);
            }
        }
    }
}
