using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AllLive.UWP.Models
{
    public class FavoriteItem: BaseNotifyPropertyChanged
    {
        public int ID { get; set; }
        public string RoomID { get; set; }
        public string UserName { get; set; }
        public string Photo { get; set; }
        public string SiteName { get; set; }

        private int _sortOrder = 0;
        public int SortOrder
        {
            get { return _sortOrder; }
            set { _sortOrder = value; DoPropertyChanged("SortOrder"); }
        }

        private bool _LiveStatus=false;
        public bool LiveStatus
        {
            get { return _LiveStatus; }
            set { _LiveStatus = value; DoPropertyChanged("LiveStatus"); }
        }

        private string _liveTitle = "";
        public string LiveTitle
        {
            get { return _liveTitle; }
            set
            {
                _liveTitle = value;
                DoPropertyChanged("LiveTitle");
                DoPropertyChanged("HasLiveTitle");
            }
        }

        public bool HasLiveTitle
        {
            get { return !string.IsNullOrWhiteSpace(LiveTitle); }
        }

        public string SiteShortName
        {
            get
            {
                switch (SiteName)
                {
                    case "哔哩哔哩直播":
                        return "哔哩";
                    case "虎牙直播":
                        return "虎牙";
                    case "斗鱼直播":
                        return "斗鱼";
                    case "抖音直播":
                        return "抖音";
                    case "Twitch直播":
                        return "Twitch";
                    default:
                        return SiteName;
                }
            }
        }

    }
}
