using AllLive.Core.Danmaku;
using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;

namespace AllLive.Core
{
    public class Douyin : ILiveSite
    {
        public string Name => "抖音直播";
        public ILiveDanmaku GetDanmaku() => new DouyinDanmaku();

        private const string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0";
        private const string ACCEPT = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";
        private const string ACCEPT_LANGUAGE = "zh-CN,zh;q=0.9,en;q=0.8";
        private const string REFERER = "https://live.douyin.com";
        private const string AUTHORITY = "live.douyin.com";

        Dictionary<string, string> headers = new Dictionary<string, string>
        {
            { "User-Agent", USER_AGENT },
            { "Referer", REFERER },
            { "Authority", AUTHORITY },
            { "Accept", ACCEPT },
            { "Accept-Language", ACCEPT_LANGUAGE }
        };

        private async Task<Dictionary<string, string>> GetRequestHeaders()
        {
            var manualCookie = CoreConfig.GetDouyinCookie();
            if (!string.IsNullOrWhiteSpace(manualCookie))
            {
                headers["Cookie"] = await ResolveCookieAsync();
                return headers;
            }
            if (headers.ContainsKey("Cookie") || headers.ContainsKey("cookie"))
            {
                return headers;
            }
            headers["Cookie"] = await ResolveCookieAsync();
            return headers;
        }

        public async Task<List<LiveCategory>> GetCategores()
        {
            List<LiveCategory> categories = new List<LiveCategory>();
            var resp = await HttpUtil.GetString("https://live.douyin.com/", await GetRequestHeaders());

            var renderData = ExtractCategoryRenderData(resp);
            if (string.IsNullOrEmpty(renderData))
            {
                throw new Exception("无法读取分类数据");
            }
            JObject renderDataJson;
            try
            {
                renderDataJson = JObject.Parse(renderData);
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Douyin] 分类数据解析失败: {ex.GetType().FullName}: {ex.Message}");
                throw;
            }
            var categoryData = renderDataJson["categoryData"] as JArray;
            if (categoryData == null)
            {
                throw new Exception("无法读取分类数据");
            }
            foreach (var item in categoryData)
            {
                List<LiveSubCategory> subs = new List<LiveSubCategory>();
                var id = $"{item["partition"]["id_str"]},{item["partition"]["type"]}";
                var subPartitions = item["sub_partition"] as JArray ?? new JArray();
                foreach (var subItem in subPartitions)
                {
                    var subCategory = new LiveSubCategory()
                    {
                        ID = $"{subItem["partition"]["id_str"]},{subItem["partition"]["type"]}",
                        Name = subItem["partition"]["title"].ToString(),
                        ParentID = id,
                        Pic = "",
                    };
                    subs.Add(subCategory);
                }
                var category = new LiveCategory()
                {
                    Children = subs,
                    ID = id,
                    Name = item["partition"]["title"].ToString(),
                };
                subs.Insert(0, new LiveSubCategory() { ID = category.ID, Name = category.Name, ParentID = category.ID, Pic = "" });
                categories.Add(category);
            }
            return categories;
        }

        private static string ExtractCategoryRenderData(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return null;
            }
            const string token = "{\\\"pathname\\\":\\\"/\\\",\\\"categoryData\\\"";
            var start = html.IndexOf(token, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            int depth = 0;
            int end = -1;
            for (int i = start; i < html.Length; i++)
            {
                var c = html[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }
            if (end < 0)
            {
                return null;
            }
            var raw = html.Substring(start, end - start + 1);
            try
            {
                return Regex.Unescape(raw);
            }
            catch
            {
                return raw.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
        }

        public async Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1)
        {
            var ids = category.ID.Split(',');
            var partitionId = ids[0];
            var partitionType = ids[1];
            var reqParams = new Dictionary<string, string> {
                    {"aid","6383"},
                    {"app_name","douyin_web" },
                    {"live_id", "1"},
                    {"device_platform","web" },
                    { "language", "zh-CN"},
                    { "enter_from", "link_share"},
                    { "cookie_enabled", "true"},
                    { "screen_width", "1980"},
                    { "screen_height", "1080"},
                    { "browser_language", "zh-CN"},
                    { "browser_platform", "Win32"},
                    { "browser_name", "Edge"},
                    { "browser_version", "125.0.0.0"},
                    {"browser_online", "true"},
                    { "count","15" },
                    { "offset", ((page - 1) * 15).ToString()},
                    {"partition",partitionId},
                    {"partition_type",partitionType},
                    {"req_from","2" }
                };
            var url = $"https://live.douyin.com/webcast/web/partition/detail/room/v2/?{Utils.BuildQueryString(reqParams)}";

            var requestUrl = await GetABougs(url);
            var resp = await HttpUtil.GetString(requestUrl,
                headers: await GetRequestHeaders()
            );
            var json = JObject.Parse(resp);
            var hasMore = (json["data"]["data"] as JArray).Count >= 15;
            var items = new List<LiveRoomItem>();
            foreach (var item in json["data"]["data"])
            {
                var roomItem = new LiveRoomItem()
                {
                    RoomID = item["web_rid"].ToString(),
                    Title = item["room"]["title"].ToString(),
                    Cover = item["room"]["cover"]["url_list"][0].ToString(),
                    UserName = item["room"]["owner"]["nickname"].ToString(),
                    Online = item["room"]["room_view_stats"]?["display_value"]?.ToObject<int>() ?? 0,
                };
                items.Add(roomItem);
            }
            return new LiveCategoryResult()
            {
                HasMore = hasMore,
                Rooms = items
            };
        }
        public async Task<LiveCategoryResult> GetRecommendRooms(int page = 1)
        {
            var reqParams = new Dictionary<string, string> {
                    {"aid","6383"},
                    {"app_name","douyin_web" },
                    {"live_id", "1"},
                    {"device_platform","web" },
                    { "language", "zh-CN"},
                    { "enter_from", "link_share"},
                    { "cookie_enabled", "true"},
                    { "screen_width", "1980"},
                    { "screen_height", "1080"},
                    { "browser_language", "zh-CN"},
                    { "browser_platform", "Win32"},
                    { "browser_name", "Edge"},
                    { "browser_version", "125.0.0.0"},
                    {"browser_online", "true"},
                    { "count","15" },
                    { "offset", ((page - 1) * 15).ToString()},
                    {"partition","720" },
                    {"partition_type","1"},
                    {"req_from","2" }
                };
            var url = $"https://live.douyin.com/webcast/web/partition/detail/room/v2/?{Utils.BuildQueryString(reqParams)}";

            var requestUrl = await GetABougs(url);
            var resp = await HttpUtil.GetString(requestUrl,
                headers: await GetRequestHeaders()
            );

            var json = JObject.Parse(resp);
            var hasMore = (json["data"]["data"] as JArray).Count >= 15;
            var items = new List<LiveRoomItem>();
            foreach (var item in json["data"]["data"])
            {
                var roomItem = new LiveRoomItem()
                {
                    RoomID = item["web_rid"].ToString(),
                    Title = item["room"]["title"].ToString(),
                    Cover = item["room"]["cover"]["url_list"][0].ToString(),
                    UserName = item["room"]["owner"]["nickname"].ToString(),
                    Online = item["room"]["room_view_stats"]?["display_value"]?.ToObject<int>() ?? 0,
                };
                items.Add(roomItem);
            }
            return new LiveCategoryResult()
            {
                HasMore = hasMore,
                Rooms = items
            };
        }
        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            // 有两种roomId，一种是webRid，一种是roomId
            // roomId是一次性的，用户每次重新开播都会生成一个新的roomId
            // roomId一般长度为19位，例如：7376429659866598196
            // webRid是固定的，用户每次开播都是同一个webRid
            // webRid一般长度为11-12位，例如：416144012050
            // 这里简单进行判断，如果roomId长度小于15，则认为是webRid
            if (roomId.ToString().Length <= 16)
            {
                var webRid = roomId as string;
                return await GetRoomDetailByWebRid(webRid);
            }

            return await GetRoomDetailByRoomID(roomId as string);
        }
        /// <summary>
        /// 通过RoomId获取直播间详情
        /// </summary>
        /// <param name="roomId">
        /// roomId是一次性的，用户每次重新开播都会生成一个新的roomId。
        /// roomId一般长度为19位，例如：7376429659866598196
        /// </param>
        /// <returns></returns>
        private async Task<LiveRoomDetail> GetRoomDetailByRoomID(string roomId)
        {
            var roomData = await GetRoomDataByRoomID(roomId);
            // 通过房间信息获取WebRid
            var webRid = roomData["data"]["room"]["owner"]["web_rid"].ToString();
            // 读取用户唯一ID，用于弹幕连接
            // 似乎这个参数不是必须的，先随机生成一个
            //var userUniqueId = await GetUserUniqueId(webRid);
            var userUniqueId = GenerateRandomNumber(12).ToString();
            var room = roomData["data"]["room"];
            var owner = room["owner"];
            var status = room["status"].ToObject<int>();
            // roomId是一次性的，用户每次重新开播都会生成一个新的roomId
            // 所以如果roomId对应的直播间状态不是直播中，就通过webRid获取直播间信息
            if (status == 4)
            {
                var result = await GetRoomDetailByWebRid(webRid);
                return result;
            }
            var roomStatus = status == 2;
            // 主要是为了获取cookie,用于弹幕websocket连接
            var headers = await GetRequestHeaders();
            return new LiveRoomDetail()
            {
                RoomID = webRid,
                Title = room["title"].ToString(),
                Cover = roomStatus ? room["cover"]["url_list"][0].ToString() : "",
                UserName = owner["nickname"].ToString(),
                UserAvatar = owner["avatar_thumb"]["url_list"][0].ToString(),
                Online = roomStatus
                  ? (int)(TryGetDouyinPopularity(room) ?? 0)
                  : 0,
                Popularity = roomStatus ? TryGetDouyinPopularity(room) : null,
                ViewerCount = roomStatus ? TryGetDouyinViewerCount(room) : null,
                Status = roomStatus,
                Url = $"https://live.douyin.com/{webRid}",
                Introduction = owner?["signature"]?.ToString() ?? "",
                Notice = "",
                DanmakuData = new DouyinDanmakuArgs()
                {
                    WebRid = webRid,
                    RoomId = roomId,
                    UserId = userUniqueId,
                    Cookie = headers["Cookie"],
                },
                Data = roomStatus ? room["stream_url"] : null,
            };

        }

        /// <summary>
        /// 通过webRid获取直播间详情
        /// </summary>
        /// <param name="webRid">
        /// webRid是固定的，用户每次开播都是同一个webRid
        /// webRid一般长度为11-12位，例如：416144012050
        /// </param>
        /// <returns></returns>
        private async Task<LiveRoomDetail> GetRoomDetailByWebRid(string webRid)
        {
            try
            {
                var result = await GetRoomDetailByWebRidApi(webRid);
                return result;
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Douyin] GetRoomDetailByWebRidApi失败 webRid={webRid} err={ex.GetType().FullName}: {ex.Message}");
            }
            return await GetRoomDetailByWebRidHtml(webRid);
        }

        private async Task<LiveRoomDetail> GetRoomDetailByWebRidApi(string webRid)
        {
            // 读取房间信息
            //var data = await _getRoomDataByApi(webRid);
            var data = await GetRoomDataApi(webRid);
            var roomData = data["data"][0];

            var userData = data["user"];
            var roomId = roomData["id_str"].ToString();

            // 读取用户唯一ID，用于弹幕连接
            // 似乎这个参数不是必须的，先随机生成一个
            //var userUniqueId = await GetUserUniqueId(webRid);
            var userUniqueId = GenerateRandomNumber(12).ToString();

            var owner = roomData["owner"];

            var roomStatus = roomData["status"].ToObject<int>() == 2;

            // 主要是为了获取cookie,用于弹幕websocket连接
            var headers = await GetRequestHeaders();
            return new LiveRoomDetail()
            {
                RoomID = webRid,
                Title = roomData["title"].ToString(),
                Cover = roomStatus ? roomData["cover"]["url_list"][0].ToString() : "",
                UserName = roomStatus
                    ? owner["nickname"].ToString()
                    : userData["nickname"].ToString(),
                UserAvatar = roomStatus
                    ? owner["avatar_thumb"]["url_list"][0].ToString()
                    : userData["avatar_thumb"]["url_list"][0].ToString(),
                Online = roomStatus
                    ? (int)(TryGetDouyinPopularity(roomData) ?? 0)
                    : 0,
                Popularity = roomStatus ? TryGetDouyinPopularity(roomData) : null,
                ViewerCount = roomStatus ? TryGetDouyinViewerCount(roomData) : null,
                Status = roomStatus,
                Url = $"https://live.douyin.com/{webRid}",
                Introduction = owner?["signature"]?.ToString() ?? "",
                Notice = "",
                DanmakuData = new DouyinDanmakuArgs()
                {
                    WebRid = webRid,
                    RoomId = roomId,
                    UserId = userUniqueId,
                    Cookie = headers["Cookie"],
                },
                Data = roomStatus ? roomData["stream_url"] : null,
            };

        }

        private async Task<LiveRoomDetail> GetRoomDetailByWebRidHtml(string webRid)
        {
            var roomData = await GetRoomDataHtml(webRid);
            var roomId = roomData["roomStore"]["roomInfo"]["room"]["id_str"].ToString();
            var userUniqueId =
                roomData["userStore"]["odin"]["user_unique_id"].ToString();

            var room = roomData["roomStore"]["roomInfo"]["room"];
            var owner = room["owner"];
            var anchor = roomData["roomStore"]["roomInfo"]["anchor"];
            var roomStatus = room["status"].ToObject<int>() == 2;

            // 主要是为了获取cookie,用于弹幕websocket连接
            var headers = await GetRequestHeaders();
            return new LiveRoomDetail()
            {
                RoomID = webRid,
                Title = room["title"].ToString(),
                Cover = roomStatus ? room["cover"]["url_list"][0].ToString() : "",
                UserName = roomStatus
                    ? owner["nickname"].ToString()
                    : anchor["nickname"].ToString(),
                UserAvatar = roomStatus
                    ? owner["avatar_thumb"]["url_list"][0].ToString()
                    : anchor["avatar_thumb"]["url_list"][0].ToString(),
                Online = roomStatus
                    ? (int)(TryGetDouyinPopularity(room) ?? 0)
                    : 0,
                Popularity = roomStatus ? TryGetDouyinPopularity(room) : null,
                ViewerCount = roomStatus ? TryGetDouyinViewerCount(room) : null,
                Status = roomStatus,
                Url = $"https://live.douyin.com/{webRid}",
                Introduction = owner?["signature"]?.ToString() ?? "",
                Notice = "",
                DanmakuData = new DouyinDanmakuArgs()
                {
                    WebRid = webRid,
                    RoomId = roomId,
                    UserId = userUniqueId,
                    Cookie = headers["Cookie"],
                },
                Data = roomStatus ? room["stream_url"] : null,
            };
        }

        private static long? TryGetDouyinPopularity(JToken room)
        {
            if (room == null)
            {
                return null;
            }

            return room["room_view_stats"]?["display_value"].ParseCountTextToLong()
                ?? room["popularity"].ParseCountTextToLong();
        }

        private static long? TryGetDouyinViewerCount(JToken room)
        {
            if (room == null)
            {
                return null;
            }

            return room["stats"]?["total_user"].ParseCountTextToLong()
                ?? room["room_view_stats"]?["total_user"].ParseCountTextToLong()
                ?? room["room_view_stats"]?["user_count"].ParseCountTextToLong()
                ?? room["total_user"].ParseCountTextToLong();
        }
        /// <summary>
        ///  进入直播间前需要先获取cookie
        /// </summary>
        /// <param name="webRid">直播间RID</param>
        /// <returns></returns>
        private async Task<string> GetWebCookie(string webRid)
        {
            var manualCookie = CoreConfig.GetDouyinCookie();
            if (!string.IsNullOrWhiteSpace(manualCookie))
            {
                return await ResolveCookieAsync(webRid);
            }
            var dyCookie = "";
            using (var resp = await HttpUtil.Head($"https://live.douyin.com/{webRid}",
                headers: await GetRequestHeaders()
            ))
            {
                if (resp.Headers.TryGetValues("Set-Cookie", out var values))
                {
                    foreach (var item in values)
                    {
                        var cookie = item.Split(';')[0];
                        if (cookie.Contains("ttwid") || cookie.Contains("__ac_nonce") || cookie.Contains("msToken"))
                        {
                            dyCookie += $"{cookie};";
                        }
                    }
                }
            }
            return dyCookie;
        }

        /// <summary>
        /// 读取用户的唯一ID
        /// 暂时无用
        /// </summary>
        /// <param name="webRid"></param>
        /// <returns></returns>
        private async Task<string> GetUserUniqueId(string webRid)
        {
            var webInfo = await GetRoomDataHtml(webRid);
            return webInfo["userStore"]["odin"]["user_unique_id"].ToString();
        }

        private async Task<JToken> GetRoomDataHtml(string webRid)
        {
            var dyCookie = await GetWebCookie(webRid);
            CoreDebug.Log($"[Douyin] GetRoomDataHtml webRid={webRid} cookieLen={dyCookie?.Length ?? 0}");
            using (var response = await HttpUtil.Get($"https://live.douyin.com/{webRid}",
                headers: new Dictionary<string, string>
                {
                    { "User-Agent", USER_AGENT },
                    { "Referer", REFERER },
                    { "Authority", AUTHORITY },
                    { "Accept", ACCEPT },
                    { "Accept-Language", ACCEPT_LANGUAGE },
                    { "Cookie", dyCookie }
                },
                ensureSuccess: false
            ))
            {
                var resp = await response.Content.ReadAsStringAsync();
                var statusCode = (int)response.StatusCode;
                CoreDebug.Log($"[Douyin] GetRoomDataHtml status={statusCode} respLen={resp?.Length ?? 0} head={TrimForLog(resp)}");
                if (!response.IsSuccessStatusCode && statusCode == 444)
                {
                    CoreDebug.Log("[Douyin] GetRoomDataHtml触发风控(444)");
                }

                var state = TryParseRenderData(resp);
                if (state != null)
                {
                    return state;
                }

                Regex regex = new Regex("\\{\\\\\"state\\\\\":\\{\\\\\"appStore.*?\\]\\\\n", RegexOptions.Singleline);
                Match match = regex.Match(resp ?? "");
                string json = match.Success ? match.Groups[0].Value : "";
                if (string.IsNullOrEmpty(json))
                {
                    CoreDebug.Log("[Douyin] GetRoomDataHtml解析失败: 未找到RENDER_DATA或state");
                    throw new Exception("无法读取直播间数据");
                }
                json = json.Trim().Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("]\\n", "");
                return JObject.Parse(json)["state"];
            }
        }

        private async Task<JToken> GetRoomDataApi(string webRid)
        {
            var reqParams = new Dictionary<string, string> {
                    {"aid","6383" },
                    {"app_name","douyin_web" },
                    {"live_id","1" },
                    {"device_platform","web" },
                    {"enter_from","web_live" },
                    {"web_rid",webRid },
                    {"room_id_str","" },
                    {"enter_source","" },
                    {"Room-Enter-User-Login-Ab","0" },
                    {"is_need_double_stream","false" },
                    {"cookie_enabled","true" },
                    {"screen_width","1980" },
                    {"screen_height","1080" },
                    {"browser_language","zh-CN" },
                    {"browser_platform","Win32" },
                    {"browser_name","Edge" },
                    {"browser_version","125.0.0.0" },
                    {"a_bogus","0" }
                };
            var url = $"https://live.douyin.com/webcast/room/web/enter/?{Utils.BuildQueryString(reqParams)}";

            var requestUrl = await GetABougs(url);
            var resp = await HttpUtil.GetString(requestUrl,
                headers: await GetRequestHeaders()
            );


           
            return JObject.Parse(resp)["data"];
        }

        private async Task<JToken> GetRoomDataByRoomID(string roomId)
        {
            var resp = await HttpUtil.GetString($"https://webcast.amemv.com/webcast/room/reflow/info/",
                headers: await GetRequestHeaders(),
                queryParameters: new Dictionary<string, string>
                {
                    {"type_id","0" },
                    {"live_id","1" },
                    {"room_id",roomId },
                    {"sec_user_id","" },
                    {"version_code","99.99.99" },
                    {"app_id","6383" },
                }
            );
            return JObject.Parse(resp);
        }

        public Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            List<LivePlayQuality> qualities = new List<LivePlayQuality>();
            if (roomDetail.Data == null)
            {
                return Task.FromResult(qualities);
            }
            var data = roomDetail.Data as JToken;
            var qulityList = data["live_core_sdk_data"]["pull_data"]["options"]["qualities"];
            var streamData = data["live_core_sdk_data"]["pull_data"]["stream_data"].ToString();

            if (!streamData.StartsWith("{"))
            {
                var flvList = (data["flv_pull_url"] as JToken).Values().Select(c => c.ToString()).ToList();
                var hlsList = (data["hls_pull_url_map"] as JToken).Values().Select(c => c.ToString()).ToList();
                foreach (var quality in qulityList)
                {
                    int level = quality["level"].ToObject<int>();
                    List<String> urls = new List<string>();
                    var flvIndex = flvList.Count - level;
                    if (flvIndex >= 0 && flvIndex < flvList.Count)
                    {
                        urls.Add(flvList[flvIndex]);
                    }
                    var hlsIndex = hlsList.Count - level;
                    if (hlsIndex >= 0 && hlsIndex < hlsList.Count)
                    {
                        urls.Add(hlsList[hlsIndex]);
                    }
                    var qualityItem = new LivePlayQuality()
                    {
                        Quality = quality["name"].ToString(),
                        Sort = level,
                        Data = urls,
                    };
                    if (urls.Count > 0)
                    {
                        qualities.Add(qualityItem);
                    }
                }
            }
            else
            {
                var qualityData = JObject.Parse(streamData)["data"] as JObject;
                foreach (var quality in qulityList)
                {
                    List<string> urls = new List<string>();

                    var flvUrl =
                        qualityData[quality["sdk_key"].ToString()]?["main"]?["flv"]?.ToString();

                    if (flvUrl != null && flvUrl.Length > 0)
                    {
                        urls.Add(flvUrl);
                    }
                    var hlsUrl =
                        qualityData[quality["sdk_key"].ToString()]?["main"]?["hls"]?.ToString();
                    if (hlsUrl != null && hlsUrl.Length > 0)
                    {
                        urls.Add(hlsUrl);
                    }
                    var qualityItem = new LivePlayQuality()
                    {
                        Quality = quality["name"].ToString(),
                        Sort = quality["level"].ToObject<int>(),
                        Data = urls,
                    };
                    if (urls.Count > 0)
                    {
                        qualities.Add(qualityItem);
                    }
                }
            }
            // var qualityData = json.decode(
            //     detail.data["live_core_sdk_data"]["pull_data"]["stream_data"])["data"];

            //qualities.sort((a, b) => b.sort.compareTo(a.sort));
            qualities = qualities.OrderByDescending(q => q.Sort).ToList();
            return Task.FromResult(qualities);
        }

        public Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn)
        {
            return Task.FromResult(qn.Data as List<string>);
        }

        public async Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            var query = new Dictionary<string, string>
            {
                { "device_platform", "webapp" },
                { "aid", "6383" },
                { "channel", "channel_pc_web" },
                { "search_channel", "aweme_live" },
                { "keyword", keyword },  // 动态值
                { "search_source", "switch_tab" },
                { "query_correct_type", "1" },
                { "is_filter_search", "0" },
                { "from_group_id", "" },
                { "offset", ((page - 1) * 10).ToString() },  // 动态计算值
                { "count", "10" },
                { "pc_client_type", "1" },
                { "version_code", "170400" },
                { "version_name", "17.4.0" },
                { "cookie_enabled", "true" },
                { "screen_width", "1980" },
                { "screen_height", "1080" },
                { "browser_language", "zh-CN" },
                { "browser_platform", "Win32" },
                { "browser_name", "Edge" },
                { "browser_version", "125.0.0.0" },
                { "browser_online", "true" },
                { "engine_name", "Blink" },
                { "engine_version", "125.0.0.0" },
                { "os_name", "Windows" },
                { "os_version", "10" },
                { "cpu_core_num", "12" },
                { "device_memory", "8" },
                { "platform", "PC" },
                { "downlink", "10" },
                { "effective_type", "4g" },
                { "round_trip_time", "100" },
                { "webid", "7382872326016435738" }
            };

            var requestUrl = $"https://www.douyin.com/aweme/v1/web/live/search/?{Utils.BuildQueryString(query)}";
            var cookie = (await GetRequestHeaders())["Cookie"];
            var headers = new Dictionary<string, string>
            {
                { "accept", "application/json, text/plain, */*" },
                { "accept-language", "zh-CN,zh;q=0.9,en;q=0.8" },
                { "cookie", cookie },
                { "priority", "u=1, i" },
                { "referer", $"https://www.douyin.com/search/{Uri.EscapeUriString(keyword)}?type=live" },
                { "sec-ch-ua", "\"Microsoft Edge\";v=\"125\", \"Chromium\";v=\"125\", \"Not.A/Brand\";v=\"24\"" },
                { "sec-ch-ua-mobile", "?0" },
                { "sec-ch-ua-platform", "\"Windows\"" },
                { "sec-fetch-dest", "empty" },
                { "sec-fetch-mode", "cors" },
                { "sec-fetch-site", "same-origin" },
                { "user-agent", USER_AGENT }
            };
            var resp = await HttpUtil.GetString(requestUrl, headers);
            var json = JObject.Parse(resp);
            var items = new List<LiveRoomItem>();
            foreach (var item in json["data"])
            {
                var itemData = JObject.Parse(item["lives"]["rawdata"].ToString());
                var roomItem = new LiveRoomItem()
                {
                    RoomID = itemData["owner"]["web_rid"].ToString(),
                    Title = itemData["title"].ToString(),
                    Cover = itemData["cover"]["url_list"][0].ToString(),
                    UserName = itemData["owner"]["nickname"].ToString(),
                    Online = itemData["stats"]["total_user"].ToObject<int>(),
                };
                items.Add(roomItem);
            }
            return new LiveSearchResult()
            {
                HasMore = items.Count >= 10,
                Rooms = items
            };
        }
        public async Task<bool> GetLiveStatus(object roomId)
        {
            var result = await GetRoomDetail(roomId: roomId);
            return result.Status;

        }
        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId)
        {
            return Task.FromResult(new List<LiveSuperChatMessage>());
        }
        private string GenerateRandomNumber(int length)
        {
            var random = new Random();
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                // 第一位不能为0
                if (i == 0)
                {
                    sb.Append(random.Next(1, 9));
                }
                else
                {
                    sb.Append(random.Next(0, 9));
                }
            }
            return sb.ToString();
        }

        private Task<string> GetABougs(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.FromResult(url);
            }
            try
            {
                var signedUrl = DouyinAbogus.BuildSignedUrl(url, USER_AGENT);
                if (!string.IsNullOrWhiteSpace(signedUrl))
                {
                    CoreDebug.Log($"[Douyin] GetABogus local success len={signedUrl.Length}");
                    return Task.FromResult(signedUrl);
                }
                CoreDebug.Log("[Douyin] GetABogus local empty");
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Douyin] GetABogus local failed err={ex.GetType().FullName}: {ex.Message}");
            }
            return Task.FromResult(url);
        }

        private static JToken TryParseRenderData(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return null;
            }
            try
            {
                var match = Regex.Match(html,
                    @"<script id=""RENDER_DATA"" type=""application\/json"">(.*?)<\/script>",
                    RegexOptions.Singleline);
                if (!match.Success)
                {
                    return null;
                }
                var renderData = match.Groups[1].Value ?? "";
                if (string.IsNullOrEmpty(renderData))
                {
                    return null;
                }
                try
                {
                    renderData = Uri.UnescapeDataString(renderData);
                }
                catch
                {
                }
                var json = JObject.Parse(renderData);
                if (json["state"] != null)
                {
                    return json["state"];
                }
                return null;
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Douyin] RENDER_DATA解析失败: {ex.GetType().FullName}: {ex.Message}");
                return null;
            }
        }

        private static string TrimForLog(string value, int maxLen = 200)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            if (value.Length <= maxLen)
            {
                return value;
            }
            return value.Substring(0, maxLen);
        }

        private static string NormalizeCookie(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            var cookie = value.Trim();
            if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            {
                cookie = cookie.Substring("Cookie:".Length).Trim();
            }
            return cookie;
        }

        private async Task<string> ResolveCookieAsync(string webRid = null)
        {
            var cookieMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var manualCookie = CoreConfig.GetDouyinCookie();
            if (!string.IsNullOrWhiteSpace(manualCookie))
            {
                cookieMap = ParseCookie(NormalizeCookie(manualCookie));
            }

            if (!cookieMap.ContainsKey("ttwid"))
            {
                using (var resp = await TryHeadAsync("https://live.douyin.com"))
                {
                    MergeCookiesFromResponse(cookieMap, resp);
                }
            }

            if (!cookieMap.ContainsKey("msToken") || !cookieMap.ContainsKey("__ac_nonce"))
            {
                var url = string.IsNullOrWhiteSpace(webRid)
                    ? "https://live.douyin.com"
                    : $"https://live.douyin.com/{webRid}";
                using (var resp = await TryHeadAsync(url))
                {
                    MergeCookiesFromResponse(cookieMap, resp);
                }
            }

            return BuildCookie(cookieMap);
        }

        private async Task<HttpResponseMessage> TryHeadAsync(string url)
        {
            try
            {
                return await HttpUtil.Head(url, headers);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }

        private static void MergeCookiesFromResponse(Dictionary<string, string> cookieMap, HttpResponseMessage resp)
        {
            if (resp == null)
            {
                return;
            }
            if (resp.Headers.TryGetValues("Set-Cookie", out var values))
            {
                foreach (var item in values)
                {
                    var pair = item.Split(';')[0];
                    MergeCookiePair(cookieMap, pair);
                }
            }
        }

        private static Dictionary<string, string> ParseCookie(string cookie)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return map;
            }
            var parts = cookie.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(new[] { '=' }, 2);
                var key = kv[0].Trim();
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                var value = kv.Length > 1 ? kv[1].Trim() : "";
                map[key] = value;
            }
            return map;
        }

        private static void MergeCookiePair(Dictionary<string, string> map, string pair)
        {
            if (string.IsNullOrWhiteSpace(pair))
            {
                return;
            }
            var kv = pair.Split(new[] { '=' }, 2);
            var key = kv[0].Trim();
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            var value = kv.Length > 1 ? kv[1].Trim() : "";
            if (!map.ContainsKey(key))
            {
                map[key] = value;
            }
        }

        private static string BuildCookie(Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0)
            {
                return "";
            }
            return string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }
}
