using Microsoft.Toolkit.Uwp.UI.Controls;
using System;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.Controls
{
    public class MyAdaptiveGridView : AdaptiveGridView
    {
        public MyAdaptiveGridView()
        {
            Loaded += MyAdaptiveGridView_Loaded;
            Unloaded += MyAdaptiveGridView_Unloaded;
        }

        private ICommand _LoadMoreCommand;
        public ICommand LoadMoreCommand
        {
            get { return _LoadMoreCommand; }
            set { _LoadMoreCommand = value; }
        }
        public bool CanLoadMore { get; set; } = false;

        public double LoadMoreBottomOffset
        {
            get { return Convert.ToDouble(GetValue(LoadMoreBottomOffsetProperty)); }
            set { SetValue(LoadMoreBottomOffsetProperty, value); }
        }

        // Using a DependencyProperty as the backing store for LoadMoreBottomOffset.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty LoadMoreBottomOffsetProperty =
            DependencyProperty.Register("LoadMoreBottomOffset", typeof(double), typeof(MyAdaptiveGridView), new PropertyMetadata(100));

        public bool DataLoading
        {
            get { return (bool)GetValue(DataLoadingProperty); }
            set { SetValue(DataLoadingProperty, value); }
        }

        // Using a DependencyProperty as the backing store for Loading.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty DataLoadingProperty =
            DependencyProperty.Register("DataLoading", typeof(bool), typeof(MyAdaptiveGridView), new PropertyMetadata(true));

        ScrollViewer scrollViewer;
        long dataLoadingCallbackToken;

        protected override void OnApplyTemplate()
        {
            DetachTemplateCallbacks();
            base.OnApplyTemplate();
            AttachTemplateCallbacks();
        }

        private void MyAdaptiveGridView_Loaded(object sender, RoutedEventArgs e)
        {
            // 缓存页面二次进入时不会重新走 OnApplyTemplate，这里补挂监听，
            // 否则 Unloaded 解绑后「滚动加载更多」在返回页面后永久失效
            AttachTemplateCallbacks();
        }

        private void MyAdaptiveGridView_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachTemplateCallbacks();
        }

        private void AttachTemplateCallbacks()
        {
            if (scrollViewer == null)
            {
                scrollViewer = GetTemplateChild("ScrollViewer") as ScrollViewer;
                if (scrollViewer != null)
                {
                    scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
                }
            }
            if (dataLoadingCallbackToken == 0)
            {
                dataLoadingCallbackToken = RegisterPropertyChangedCallback(DataLoadingProperty, OnDataLoadingChanged);
            }
        }

        private void DetachTemplateCallbacks()
        {
            if (scrollViewer != null)
            {
                scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;
                scrollViewer = null;
            }
            if (dataLoadingCallbackToken != 0)
            {
                UnregisterPropertyChangedCallback(DataLoadingProperty, dataLoadingCallbackToken);
                dataLoadingCallbackToken = 0;
            }
        }

        private void OnDataLoadingChanged(DependencyObject obj, DependencyProperty property)
        {
            if (DataLoading)
            {
                return;
            }
            // 低优先级延后一拍，等本轮布局完成后再判断内容是否填满视口
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                TryLoadMoreWhenContentDoesNotFill();
            });
        }

        private void TryLoadMoreWhenContentDoesNotFill()
        {
            if (!DataLoading && CanLoadMore && scrollViewer != null && scrollViewer.ScrollableHeight == 0)
            {
                LoadMoreCommand?.Execute(null);
            }
        }

        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (scrollViewer == null || e.IsIntermediate)
            {
                return;
            }
            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - LoadMoreBottomOffset && CanLoadMore)
            {
                LoadMoreCommand?.Execute(null);
            }

        }
    }
}
