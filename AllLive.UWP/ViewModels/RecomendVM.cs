using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using AllLive.UWP.Helper;

namespace AllLive.UWP.ViewModels
{
    public class RecomendVM : BaseViewModel
    {
        public RecomendVM()
        {
            List<RecomendItemVM> ls = new List<RecomendItemVM>();
            foreach (var item in MainVM.Sites)
            {
                ls.Add(new RecomendItemVM(item));
            }
            Items = ls;
        }

        private List<RecomendItemVM> items;
        public List<RecomendItemVM> Items
        {
            get { return items; }
            set { items = value; DoPropertyChanged("Items"); }
        }


    }
    public class RecomendItemVM : BaseViewModel
    {
        public readonly Site site;
        public RecomendItemVM(Site site)
        {
            this.site = site;
            Items = new ObservableCollection<Core.Models.LiveRoomItem>();
        }
        public ObservableCollection<AllLive.Core.Models.LiveRoomItem> Items { get; set; }
        private readonly PagedRequestState requests = new PagedRequestState();
        private bool canLoadMoreBeforeRequest;

        public void CancelLoad()
        {
            if (!Loading) return;
            requests.Cancel();
            Loading = false;
            CanLoadMore = canLoadMoreBeforeRequest;
        }

        public async void LoadData()
        {
            var request = requests.TryBegin();
            if (request == null) return;
            canLoadMoreBeforeRequest = CanLoadMore;
            try
            {
                Loading = true;
                CanLoadMore = false;
                IsEmpty = false;
                var result = await site.LiveSite.GetRecommendRooms(request.Page, request.Token);
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
            requests.Reset();
            base.Refresh();
            Items.Clear();
            LoadData();
        }

        public override void LoadMore()
        {
            if (CanLoadMore && !Loading) LoadData();
        }
    }
}
