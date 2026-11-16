using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AllLive.Core.Danmaku;
using AllLive.Core.Helper;
using Newtonsoft.Json.Linq;
using System.Linq;


/*
 * 参考：
 * https://github.com/wbt5/real-url/blob/master/douyu.py
 */
namespace AllLive.Core
{
    public class Douyu : ILiveSite, ILivePlayInfoSite
    {
        public string Name => "斗鱼直播";
        public ILiveDanmaku GetDanmaku() => new DouyuDanmaku();
        public async Task<List<LiveCategory>> GetCategores(CancellationToken cancellationToken = default(CancellationToken))
        {
            List<LiveCategory> categories = new List<LiveCategory>();
            var result = await HttpUtil.GetString("https://m.douyu.com/api/cate/list", cancellationToken: cancellationToken);
            var obj = JObject.Parse(result);
            var cate1 = obj["data"]["cate1Info"] as JArray;
            var cate2 = obj["data"]["cate2Info"] as JArray;
            foreach (var item in cate1)
            {
                var cate1Id = item["cate1Id"].ToString();
                var cate1Name = item["cate1Name"].ToString();
                List<LiveSubCategory> subCategories = new List<LiveSubCategory>();
                cate2.Where(x => x["cate1Id"].ToString() == cate1Id).ToList().ForEach(element =>
                {
                    subCategories.Add(new LiveSubCategory()
                    {
                        Pic = element["icon"].ToString(),
                        ID = element["cate2Id"].ToString(),
                        ParentID = cate1Id,
                        Name = element["cate2Name"].ToString(),
                    });
                });
               
                categories.Add(
                  new LiveCategory()
                  {
                      ID = cate1Id,
                      Name = cate1Name,
                      // 只取前30个子分类
                      Children = subCategories.Take(30).ToList()
                  }
                );
            }
            categories.Sort((x, y) => x.ID.CompareTo(y.ID));
            return categories;
        }

      
        public async Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1, CancellationToken cancellationToken = default(CancellationToken))
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/gapi/rkc/directory/mixList/2_{ category.ID}/{page}", cancellationToken: cancellationToken);
            var obj = JObject.Parse(result);

            foreach (var item in obj["data"]["rl"])
            {
                if (item["type"].ToInt32() == 1)
                    categoryResult.Rooms.Add(new LiveRoomItem()
                    {
                        Cover = item["rs16"].ToString(),
                        Online = item["ol"].ToInt32(),
                        RoomID = item["rid"].ToString(),
                        Title = item["rn"].ToString(),
                        UserName = item["nn"].ToString(),
                    });
            }
            categoryResult.HasMore = page < obj["data"]["pgcnt"].ToInt32();
            return categoryResult;
        }
        public async Task<LiveCategoryResult> GetRecommendRooms(int page = 1, CancellationToken cancellationToken = default(CancellationToken))
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/japi/weblist/apinc/allpage/6/{page}", cancellationToken: cancellationToken);
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"]["rl"])
            {
                categoryResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = item["rs16"].ToString(),
                    Online = item["ol"].ToInt32(),
                    RoomID = item["rid"].ToString(),
                    Title = item["rn"].ToString(),
                    UserName = item["nn"].ToString(),
                });
            }
            categoryResult.HasMore = page < obj["data"]["pgcnt"].ToInt32();
            return categoryResult;
        }
        public async Task<LiveRoomDetail> GetRoomDetail(object roomId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var roomInfo = await GetRoomInfo(roomId.ToString(), cancellationToken: cancellationToken);
            CoreDebug.Log(() => $"[Douyu] GetRoomDetail roomId={roomId} show_status={roomInfo["show_status"]} videoLoop={roomInfo["videoLoop"]} room_name={roomInfo["room_name"]}");
           

            return new LiveRoomDetail()
            {
                Cover = roomInfo["room_pic"].ToString(),
                Online = ParseHotNum(roomInfo["room_biz_all"]["hot"].ToString()),
                Popularity = ParseHotNum(roomInfo["room_biz_all"]["hot"].ToString()),
                ViewerCount = TryGetViewerCount(roomInfo),
                RoomID = roomInfo["room_id"].ToString(),
                Title = roomInfo["room_name"].ToString(),
                UserName = roomInfo["owner_name"].ToString(),
                UserAvatar = roomInfo["owner_avatar"].ToString(),
                Introduction =roomInfo["show_details"].ToString(),
                Notice = "",
                Status = roomInfo["show_status"].ToInt32() == 1 && roomInfo["videoLoop"].ToInt32() != 1 && !LiveStatusParser.IsReplayRoomName(roomInfo["room_name"]?.ToString()),
                DanmakuData = roomInfo["room_id"].ToString(),
                // Playback credentials are prepared lazily; metadata refresh never signs a stream.
                Data = null,
                Url = "https://www.douyu.com/" + roomId,
                IsRecord= roomInfo["videoLoop"].ToInt32() == 1,
            };
        }

        private static long? TryGetViewerCount(JToken roomInfo)
        {
            if (roomInfo == null)
            {
                return null;
            }

            return roomInfo["viewer_num"].ParseCountTextToLong()
                ?? roomInfo["online"].ParseCountTextToLong()
                ?? roomInfo["online_num"].ParseCountTextToLong()
                ?? roomInfo["show_num"].ParseCountTextToLong()
                ?? roomInfo["show_num_v2"].ParseCountTextToLong()
                ?? roomInfo["room_biz_all"]?["viewer_num"].ParseCountTextToLong()
                ?? roomInfo["room_biz_all"]?["online"].ParseCountTextToLong();
        }


        private async Task<JToken> GetRoomInfo(string roomId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var headers = new Dictionary<string, string>()
            {
                { "referer", $"https://www.douyu.com/{roomId}"},
                { "user-agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36 Edg/114.0.1823.43" },
            };
            ApplyDeviceCookie(headers);
            var result = await HttpUtil.GetString($"https://www.douyu.com/betard/{roomId}", headers, cancellationToken: cancellationToken);
            var obj = JObject.Parse(result);
            return obj["room"];
        }

        // 匿名设备身份：dy_did/acf_did 使用长期稳定的设备 ID（由宿主设置），避免每次都像新设备访问而触发风控。
        private static void ApplyDeviceCookie(IDictionary<string, string> headers)
        {
            var deviceId = CoreConfig.GetDouyuDeviceId();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                headers["cookie"] = $"dy_did={deviceId}; acf_did={deviceId}";
            }
        }

        public async Task<LiveSearchResult> Search(string keyword, int page = 1, CancellationToken cancellationToken = default(CancellationToken))
        {
            LiveSearchResult searchResult = new LiveSearchResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/japi/search/api/searchShow?kw={ Uri.EscapeDataString(keyword)}&page={ page}&pageSize=20", cancellationToken: cancellationToken);
            var obj = JObject.Parse(result);

            foreach (var item in obj["data"]["relateShow"])
            {
                searchResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = item["roomSrc"].ToString(),
                    Online = ParseHotNum(item["hot"].ToString()),
                    RoomID = item["rid"].ToString(),
                    Title = item["roomName"].ToString(),
                    UserName = item["nickName"].ToString(),
                });
            }
            searchResult.HasMore = ((JArray)obj["data"]["relateShow"]).Count > 0;
            return searchResult;
        }
        private readonly DouyuPlaybackClient playback = new DouyuPlaybackClient();

        public Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail, CancellationToken cancellationToken = default(CancellationToken))
        {
            return playback.GetQualitiesAsync(roomDetail.RoomID, cancellationToken);
        }

        public async Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality quality, CancellationToken cancellationToken = default(CancellationToken))
        {
            var urls = await GetPlayUrlsWithInfoAsync(roomDetail, quality, cancellationToken).ConfigureAwait(false);
            return urls.Select(x => x.Url).ToList();
        }

        public Task<List<LivePlayUrl>> GetPlayUrlsWithInfoAsync(LiveRoomDetail room, LivePlayQuality quality, CancellationToken token)
        {
            return playback.GetUrlsAsync(room.RoomID, quality, token);
        }

        public async Task<bool> GetLiveStatus(object roomId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var roomInfo = await GetRoomInfo(roomId.ToString(), cancellationToken: cancellationToken);
            var showStatus = roomInfo?["show_status"];
            var loop = roomInfo?["videoLoop"];
            // 官方赛事回放房（room_name 以【回放】开头）即使 show_status=1 也按未开播处理。
            var replayRoom = LiveStatusParser.IsReplayRoomName(roomInfo?["room_name"]?.ToString());
            var live = LiveStatusParser.IsLive(showStatus, 1, 0, 2)
                && (loop == null || loop.Type == JTokenType.Null || LiveStatusParser.ReadRequiredNumber(loop) != 1)
                && !replayRoom;
            CoreDebug.Log(() => $"[Douyu] GetLiveStatus roomId={roomId} show_status={showStatus} videoLoop={loop} replayRoom={replayRoom} => {live}");
            return live;
        }
        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.FromResult(new List<LiveSuperChatMessage>());
        }

        private int ParseHotNum(string hn)
        {
            try
            {
                var num = double.Parse(hn.Replace("万", ""));
                if (hn.Contains("万"))
                {
                    num = num * 10000;
                }
                return int.Parse(num.ToString());
            }
            catch (Exception)
            {
                return -999;
            }

        }
    }


}
