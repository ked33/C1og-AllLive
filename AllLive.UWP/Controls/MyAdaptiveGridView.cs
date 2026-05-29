using Microsoft.Toolkit.Uwp.UI.Controls;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace AllLive.UWP.Controls
{
    public class MyAdaptiveGridView : AdaptiveGridView
    {
        public MyAdaptiveGridView()
        {
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
            scrollViewer = GetTemplateChild("ScrollViewer") as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
            }
            dataLoadingCallbackToken = RegisterPropertyChangedCallback(DataLoadingProperty, OnDataLoadingChanged);
        }

        private void MyAdaptiveGridView_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachTemplateCallbacks();
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
            TryLoadMoreWhenContentDoesNotFill();
        }

        private void TryLoadMoreWhenContentDoesNotFill()
        {
            if (!DataLoading && scrollViewer != null && scrollViewer.ScrollableHeight == 0)
            {
                LoadMoreCommand?.Execute(null);
            }
        }

        private void MyAdaptiveGridView_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {

            Debug.WriteLine("内容变更");
            TryLoadMoreWhenContentDoesNotFill();
        }

        private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (scrollViewer == null)
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
