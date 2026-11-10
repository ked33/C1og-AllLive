using AllLive.UWP.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AllLive.UWP.ViewModels
{
    public class CategoryDetailVM:BaseViewModel
    {
        public CategoryDetailVM()
        {
            Items = new ObservableCollection<Core.Models.LiveRoomItem>();
        }

        public ObservableCollection<AllLive.Core.Models.LiveRoomItem> Items { get; set; }

        
        private ILiveSite _site;
        public ILiveSite Site
        {
            get { return _site; }
            set { _site = value; }
        }
        private LiveSubCategory _category;
        public LiveSubCategory Category
        {
            get { return _category; }
            set { _category = value; }
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

        public async void LoadData(ILiveSite site, LiveSubCategory category)
        {
            if (site == null || category == null) return;
            if (!ReferenceEquals(Site, site) || !ReferenceEquals(Category, category))
            {
                requests.Reset();
                Items.Clear();
                Page = 1;
                Site = site;
                Category = category;
            }
            var request = requests.TryBegin();
            if (request == null) return;
            canLoadMoreBeforeRequest = CanLoadMore;
            try
            {
                Loading = true;
                CanLoadMore = false;
                IsEmpty = false;
                var result = await site.GetCategoryRooms(category, request.Page, request.Token);
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
            LoadData(_site, _category);
        }

        public override void LoadMore()
        {
            if (CanLoadMore && !Loading) LoadData(_site, _category);
        }
    }
}
