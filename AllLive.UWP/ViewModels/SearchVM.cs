using AllLive.UWP.Helper;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AllLive.UWP.ViewModels
{
    public  class SearchVM:BaseViewModel
    {
        public SearchVM()
        {
            List<SearchItemVM> ls = new List<SearchItemVM>();
            foreach (var item in MainVM.Sites)
            {
                ls.Add(new SearchItemVM(item));
            }
            Items = ls;
        }

        private List<SearchItemVM> items;
        public List<SearchItemVM> Items
        {
            get { return items; }
            set { items = value; DoPropertyChanged("Items"); }
        }
     
    }
    public class SearchItemVM : BaseViewModel
    {
        public readonly Site site;
        public SearchItemVM(Site site)
        {
            this.site = site;
             Items = new ObservableCollection<Core.Models.LiveRoomItem>();
        }
        public ObservableCollection<AllLive.Core.Models.LiveRoomItem> Items { get; set; }
        private string _keyword="";
        public string Keyword => _keyword;
        public void SetQuery(string keyword)
        {
            requests.Reset();
            _keyword = keyword ?? string.Empty;
            Page = 1;
            Loading = false;
            CanLoadMore = false;
            Items.Clear();
            IsEmpty = false;
        }

        private readonly PagedRequestState requests = new PagedRequestState();
        private bool canLoadMoreBeforeRequest;

        public void CancelLoad()
        {
            if (!Loading) return;
            requests.Cancel();
            Loading = false;
            CanLoadMore = canLoadMoreBeforeRequest;
        }

        public async void LoadData(string keyword)
        {
            if (!string.Equals(_keyword, keyword, StringComparison.Ordinal)) SetQuery(keyword);
            if (string.IsNullOrWhiteSpace(_keyword)) return;
            var request = requests.TryBegin();
            if (request == null) return;
            canLoadMoreBeforeRequest = CanLoadMore;
            try
            {
                Loading = true;
                CanLoadMore = false;
                IsEmpty = false;
                var result = await site.LiveSite.Search(_keyword, request.Page, request.Token);
                if (!requests.IsCurrent(request)) return;
                var knownRooms = new HashSet<string>(Items.Select(item => item.RoomID), StringComparer.OrdinalIgnoreCase);
                var added = 0;
                foreach (var item in result.Rooms)
                {
                    if (item != null && knownRooms.Add(item.RoomID))
                    {
                        Items.Add(item);
                        added++;
                    }
                }
                IsEmpty = Items.Count == 0;
                CanLoadMore = result.HasMore && added > 0;
                requests.Accept(request);
                Page = requests.Page;
            }
            catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (requests.IsCurrent(request))
                {
                    CanLoadMore = canLoadMoreBeforeRequest;
                    HandleError(ex);
                }
            }
            finally
            {
                var current = requests.IsCurrent(request);
                requests.End(request);
                if (current) Loading = false;
            }
        }

        public override void Refresh()
        {
            SetQuery(_keyword);
            LoadData(_keyword);
        }

        public override void LoadMore()
        {
            if (CanLoadMore && !Loading) LoadData(_keyword);
        }
    }
}
