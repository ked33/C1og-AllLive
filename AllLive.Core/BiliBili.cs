using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AllLive.Core.Danmaku;
using AllLive.Core.Helper;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using WebSocketSharp;
using System.Linq;
using Newtonsoft.Json;
using System.Web;
using System.Net;
using System.Net.Http;

namespace AllLive.Core
{

    public class BiliBili : ILiveSite
    {
        public string Name => "哔哩哔哩直播";
        public ILiveDanmaku GetDanmaku() => new BiliBiliDanmaku();
        /// <summary>
        /// 哔哩哔哩Cookie
        /// </summary>
        public string Cookie { get; set; }
        /// <summary>
        /// 哔哩哔哩用户ID
        /// </summary>
        public long UserId { get; set; }


        private string buvid3 = "";
        private string buvid4 = "";
        private async Task<Dictionary<string, string>> GetRequestHeader()
        {

            if (string.IsNullOrEmpty(buvid3))
            {
                var buvid = await GetBuvid();
                buvid3 = buvid.Item1;
                buvid4 = buvid.Item2;
            }

            var headers = new Dictionary<string, string>()
            {
                {"user-agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0" },
                {"referer","https://live.bilibili.com/" },
                { "cookie",GetCookie()}
            };

            return headers;
        }

        private string GetCookie()
        {
            var _cookie = "";
            if (string.IsNullOrEmpty(Cookie))
            {
                _cookie = $"buvid3={buvid3};buvid4={buvid4};";
            }
            else
            {
                _cookie = Cookie.Contains("buvid3") ? Cookie : $"{Cookie};buvid3={buvid3};buvid4={buvid4}";
            }
            return _cookie;
        }

        public async Task<List<LiveCategory>> GetCategores()
        {
            List<LiveCategory> categories = new List<LiveCategory>();
            var result = await HttpUtil.GetString("https://api.live.bilibili.com/room/v1/Area/getList?need_entrance=1&parent_id=0", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"])
            {
                List<LiveSubCategory> subs = new List<LiveSubCategory>();
                foreach (var subItem in item["list"])
                {
                    subs.Add(new LiveSubCategory()
                    {
                        Pic = subItem["pic"].ToString() + "@100w.png",
                        ID = subItem["id"].ToString(),
                        ParentID = subItem["parent_id"].ToString(),
                        Name = subItem["name"].ToString(),
                    });
                }

                categories.Add(new LiveCategory()
                {
                    Children = subs,
                    ID = item["id"].ToString(),
                    Name = item["name"].ToString(),
                });
            }
            return categories;
        }
        public async Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var url = $"https://api.live.bilibili.com/xlive/web-interface/v1/second/getList";
            var accessId = await GetAssessId();
            var query = $"platform=web&parent_area_id={category.ParentID}&area_id={category.ID}&sort_type=&page={page}&w_webid={accessId}";
            query = await GetWbiSign(query);
            var result = await HttpUtil.GetString($"{url}?{query}", headers: await GetRequestHeader());

            var obj = JObject.Parse(result);
            categoryResult.HasMore = obj["data"]["has_more"].ToInt32() == 1;
            foreach (var item in obj["data"]["list"])
            {
                categoryResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = item["cover"].ToString() + "@300w.jpg",
                    Online = item["online"].ToInt32(),
                    RoomID = item["roomid"].ToString(),
                    Title = item["title"].ToString(),
                    UserName = item["uname"].ToString(),
                });
            }
            return categoryResult;
        }
        public async Task<LiveCategoryResult> GetRecommendRooms(int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var url = $"https://api.live.bilibili.com/xlive/web-interface/v1/second/getListByArea";
            var query = $"platform=web&sort=online&page_size=30&page={page}";
            query = await GetWbiSign(query);
            var result = await HttpUtil.GetString($"{url}?{query}", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            categoryResult.HasMore = ((JArray)obj["data"]["list"]).Count > 0;
            foreach (var item in obj["data"]["list"])
            {
                categoryResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = item["cover"].ToString() + "@300w.jpg",
                    Online = item["online"].ToInt32(),
                    RoomID = item["roomid"].ToString(),
                    Title = item["title"].ToString(),
                    UserName = item["uname"].ToString(),
                });
            }
            return categoryResult;
        }
        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            var url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom";
            var query = $"room_id={roomId}";
            query = await GetWbiSign(query);
            var result = await HttpUtil.GetString($"{url}?{query}", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            var roomInfo = obj["data"]["room_info"];
            var popularity = roomInfo["online"].ParseCountTextToLong() ?? 0;
            const string popularitySource = "getInfoByRoom.room_info.online";
            var isLive = roomInfo["live_status"].ToInt32() == 1;
            var viewerCountFromApi = TryGetViewerCountFromApi(obj["data"]);
            var viewerCountFromUid = (long?)null;
            var viewerCountFromHtml = (long?)null;
            var viewerCount = viewerCountFromApi;
            var viewerCountSource = viewerCountFromApi.HasValue ? "getInfoByRoom.watched_show" : null;
            if (!viewerCount.HasValue)
            {
                viewerCountFromUid = await TryGetViewerCountByUid(obj["data"]["anchor_info"]?["base_info"]?["uid"].ToString());
                viewerCount = viewerCountFromUid;
                if (viewerCount.HasValue)
                {
                    viewerCountSource = "get_status_info_by_uids.online";
                }
            }
            if (!viewerCount.HasValue && isLive)
            {
                viewerCountFromHtml = await TryGetViewerCountFromHtml(roomId.ToString());
                viewerCount = viewerCountFromHtml;
                if (viewerCount.HasValue)
                {
                    viewerCountSource = "live_page.watched_show";
                }
            }
            CoreDebug.Log($"[Bilibili] 人数来源 roomId={roomId} get_status_info_by_uids={FormatNullableLong(viewerCountFromUid)} watched_show={FormatNullableLong(viewerCountFromApi)} html_watched_show={FormatNullableLong(viewerCountFromHtml)} getInfoByRoom.room_info.online={popularity} chosenViewerCount={FormatNullableLong(viewerCount)} viewerCountSource={viewerCountSource ?? "null"}");

            return new LiveRoomDetail()
            {
                Cover = roomInfo["cover"].ToString(),
                Online = popularity > int.MaxValue ? int.MaxValue : (int)popularity,
                Popularity = popularity,
                ViewerCount = viewerCount,
                PopularitySource = popularitySource,
                ViewerCountSource = viewerCountSource,
                RoomID = roomInfo["room_id"].ToString(),
                Title = roomInfo["title"].ToString(),
                UserName = obj["data"]["anchor_info"]["base_info"]["uname"].ToString(),
                Introduction = roomInfo["description"].ToString(),
                UserAvatar = obj["data"]["anchor_info"]["base_info"]["face"].ToString() + "@100w.jpg",
                Notice = "",
                Status = isLive,
                DanmakuData = new BiliDanmakuArgs()
                {
                    RoomId = roomInfo["room_id"].ToInt32(),
                    UserId = UserId,
                    Cookie = GetCookie(),
                },
                Url = "https://live.bilibili.com/" + roomId
            };
        }

        private static long? TryGetViewerCountFromApi(JToken data)
        {
            if (data == null)
            {
                return null;
            }

            return data["watched_show"]?["num"].ParseCountTextToLong()
                ?? data["watched_show"]?["text_small"].ParseCountTextToLong()
                ?? data["watched_show"]?["text_large"].ParseCountTextToLong()
                ?? data["room_info"]?["watched_show"]?["num"].ParseCountTextToLong()
                ?? data["room_info"]?["watched_show"]?["text_small"].ParseCountTextToLong()
                ?? data["room_info"]?["watched_show"]?["text_large"].ParseCountTextToLong();
        }

        private async Task<long?> TryGetViewerCountByUid(string uid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(uid))
                {
                    return null;
                }

                var result = await HttpUtil.GetString($"https://api.live.bilibili.com/room/v1/Room/get_status_info_by_uids?uids[]={uid}", headers: await GetRequestHeader());
                var obj = JObject.Parse(result);
                return obj["data"]?[uid]?["online"].ParseCountTextToLong();
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Bilibili] 通过uid读取在线人数失败 uid={uid} err={ex.GetType().FullName}: {ex.Message}");
                return null;
            }
        }

        private async Task<long?> TryGetViewerCountFromHtml(string roomId)
        {
            try
            {
                var html = await HttpUtil.GetString($"https://live.bilibili.com/{roomId}", headers: await GetRequestHeader());
                if (string.IsNullOrWhiteSpace(html))
                {
                    return null;
                }

                var watchedShowJson = html.MatchTextSingleline(@"""watched_show"":(\{.*?\})", "");
                if (string.IsNullOrWhiteSpace(watchedShowJson))
                {
                    return null;
                }

                var watchedShow = JObject.Parse(watchedShowJson);
                return watchedShow["num"].ParseCountTextToLong()
                    ?? watchedShow["text_small"].ParseCountTextToLong()
                    ?? watchedShow["text_large"].ParseCountTextToLong();
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Bilibili] 读取观看人数失败 roomId={roomId} err={ex.GetType().FullName}: {ex.Message}");
                return null;
            }
        }

        private static string FormatNullableLong(long? value)
        {
            return value.HasValue ? value.Value.ToString() : "null";
        }
        public async Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            LiveSearchResult searchResult = new LiveSearchResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://api.bilibili.com/x/web-interface/search/type?context=&search_type=live&cover_type=user_cover&page={page}&order=&keyword={Uri.EscapeDataString(keyword)}&category_id=&__refresh__=true&_extra=&highlight=0&single_column=0", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);

            foreach (var item in obj["data"]["result"]["live_room"])
            {
                searchResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = "https:" + item["cover"].ToString() + "@300w.jpg",
                    Online = item["online"].ToInt32(),
                    RoomID = item["roomid"].ToString(),
                    Title = Regex.Replace(item["title"].ToString(), @"<em.*?/em>", ""),
                    UserName = item["uname"].ToString(),
                });
            }
            searchResult.HasMore = searchResult.Rooms.Count > 0;
            return searchResult;
        }
        public async Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            try
            {
                var qualities = await GetPlayQualityNew(roomDetail.RoomID);
                if (qualities != null && qualities.Count > 0)
                {
                    return qualities;
                }
                CoreDebug.Log($"[Bilibili] GetPlayQualityNew返回空列表 roomId={roomDetail?.RoomID}，回退旧接口");
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Bilibili] GetPlayQualityNew失败 roomId={roomDetail?.RoomID} err={ex.GetType().FullName} {ex.Message}，回退旧接口");
            }
            return await GetPlayQualityOld(roomDetail.RoomID);
        }
        /// <summary>
        /// 新的获取清晰度方式，优先走 web-room playinfo
        /// </summary>
        /// <param name="roomID"></param>
        /// <returns></returns>
        private async Task<List<LivePlayQuality>> GetPlayQualityNew(string roomID)
        {
            List<LivePlayQuality> qualities = new List<LivePlayQuality>();
            var client = await CreateBilibiliPlayInfoHttpClientAsync();
            using (client)
            {
                var obj = await RequestBilibiliPlayInfoAsync(client, roomID, null);
                var playurl = obj?["data"]?["playurl_info"]?["playurl"];
                if (playurl == null)
                {
                    throw new Exception("playurl_info.playurl为空");
                }
                var qualitiesMap = new Dictionary<int, string>();
                foreach (var item in playurl["g_qn_desc"] ?? new JArray())
                {
                    qualitiesMap[item["qn"].ToObject<int>()] = item["desc"]?.ToString() ?? "未知清晰度";
                }
                foreach (var item in playurl["stream"]?[0]?["format"]?[0]?["codec"]?[0]?["accept_qn"] ?? new JArray())
                {
                    var qnValue = item.ToObject<int>();
                    var qualityText = qualitiesMap.ContainsKey(qnValue) ? qualitiesMap[qnValue] : "未知清晰度";
                    var qualityItem = new LivePlayQuality()
                    {
                        Quality = qualityText,
                        Data = item,
                    };
                    qualities.Add(qualityItem);
                }
            }
            return qualities;
        }

        /// <summary>
        /// 旧的获取清晰度方式，部分直播看不了
        /// </summary>
        /// <param name="roomID"></param>
        /// <returns></returns>
        private async Task<List<LivePlayQuality>> GetPlayQualityOld(string roomID)
        {

            List<LivePlayQuality> qualities = new List<LivePlayQuality>();
            var result = await HttpUtil.GetString($"https://api.live.bilibili.com/room/v1/Room/playUrl?cid={roomID}&qn=&platform=web", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"]["quality_description"])
            {
                qualities.Add(new LivePlayQuality()
                {
                    Quality = item["desc"].ToString(),
                    Data = item["qn"].ToInt32(),
                });
            }
            return qualities;
        }

        public async Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn)
        {
            try
            {
                var urls = await GetPlayUrlsNew(roomDetail.RoomID, qn.Data);
                if (urls != null && urls.Count > 0)
                {
                    return urls;
                }
                CoreDebug.Log($"[Bilibili] GetPlayUrlsNew返回空列表 roomId={roomDetail?.RoomID} qn={qn?.Data}，回退旧接口");
            }
            catch (Exception ex)
            {
                CoreDebug.Log($"[Bilibili] GetPlayUrlsNew失败 roomId={roomDetail?.RoomID} qn={qn?.Data} err={ex.GetType().FullName} {ex.Message}，回退旧接口");
            }
            return await GetPlayUrlsOld(roomDetail.RoomID, qn.Data);
        }
        private async Task<List<string>> GetPlayUrlsNew(string roomID, object qn)
        {
            var qnValue = TryConvertToInt(qn);
            var urls = new List<string>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var client = await CreateBilibiliPlayInfoHttpClientAsync();
            using (client)
            {
                var obj = await RequestBilibiliPlayInfoAsync(client, roomID, qnValue);
                var streamList = obj?["data"]?["playurl_info"]?["playurl"]?["stream"] as JArray;
                if (streamList == null)
                {
                    throw new Exception("playurl_info.playurl.stream为空");
                }

                foreach (var streamItem in streamList)
                {
                    var formatList = streamItem["format"] as JArray;
                    if (formatList == null)
                    {
                        continue;
                    }

                    foreach (var formatItem in formatList)
                    {
                        var codecList = formatItem["codec"] as JArray;
                        if (codecList == null)
                        {
                            continue;
                        }

                        foreach (var codecItem in codecList)
                        {
                            var baseUrl = codecItem["base_url"]?.ToString();
                            if (string.IsNullOrWhiteSpace(baseUrl))
                            {
                                continue;
                            }

                            var urlInfo = codecItem["url_info"] as JArray;
                            if (urlInfo == null)
                            {
                                continue;
                            }

                            foreach (var urlItem in urlInfo)
                            {
                                var host = urlItem["host"]?.ToString() ?? string.Empty;
                                var extra = urlItem["extra"]?.ToString() ?? string.Empty;
                                var fullUrl = $"{host}{baseUrl}{extra}";
                                if (string.IsNullOrWhiteSpace(fullUrl) || !dedupe.Add(fullUrl))
                                {
                                    continue;
                                }

                                urls.Add(fullUrl);
                            }
                        }
                    }
                }
            }

            var orderedUrls = OrderBilibiliPlayUrls(urls);
            CoreDebug.Log($"[Bilibili] PlayInfo直出 roomId={roomID} qn={qnValue?.ToString() ?? "null"} total={orderedUrls.Count}");
            return orderedUrls;
        }
        /// <summary>
        /// 旧的获取播放地址方式，部分直播看不了
        /// </summary>
        /// <param name="roomID"></param>
        /// <param name="qn"></param>
        /// <returns></returns>
        private async Task<List<string>> GetPlayUrlsOld(string roomID, object qn)
        {
            List<string> urls = new List<string>();
            var result = await HttpUtil.GetString($"https://api.live.bilibili.com/room/v1/Room/playUrl?cid={roomID}&qn={qn}&platform=web", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"]["durl"])
            {
                urls.Add(item["url"].ToString());
            }
            return OrderBilibiliPlayUrls(urls);
        }

        private sealed class BilibiliPlayUrlCandidate
        {
            public string Url { get; set; }
            public bool IsFlv { get; set; }
            public bool IsHls { get; set; }
        }

        private async Task<HttpClient> CreateBilibiliPlayInfoHttpClientAsync()
        {
            var headers = await GetRequestHeader();
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = false,
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            foreach (var item in headers)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(item.Key, item.Value);
            }
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
            return client;
        }

        private async Task<JObject> RequestBilibiliPlayInfoAsync(HttpClient client, string roomID, int? qn)
        {
            var queryParameters = new Dictionary<string, string>()
            {
                { "room_id", roomID },
                { "protocol", "0,1" },
                { "format", "0,2" },
                { "codec", "0" },
                { "platform", "web" },
                { "dolby", "5" },
            };
            if (qn.HasValue)
            {
                queryParameters["qn"] = qn.Value.ToString();
            }
            var query = string.Join("&", queryParameters.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value ?? string.Empty)}"));
            var requestUrl = $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?{query}";
            var response = await client.GetAsync(requestUrl);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(result);
            if (obj["code"]?.ToObject<int>() != 0)
            {
                throw new Exception($"getRoomPlayInfo返回异常 code={obj["code"]} message={obj["message"] ?? obj["msg"]}");
            }
            return obj;
        }

        private static List<BilibiliPlayUrlCandidate> ParseBilibiliPlayUrlCandidates(JToken playurl)
        {
            var result = new List<BilibiliPlayUrlCandidate>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var streamList = playurl?["stream"] as JArray;
            if (streamList == null)
            {
                return result;
            }

            foreach (var streamItem in streamList)
            {
                var protocolName = streamItem["protocol_name"]?.ToString() ?? string.Empty;
                var formatList = streamItem["format"] as JArray;
                if (formatList == null)
                {
                    continue;
                }

                foreach (var formatItem in formatList)
                {
                    var formatName = formatItem["format_name"]?.ToString() ?? string.Empty;
                    var codecList = formatItem["codec"] as JArray;
                    if (codecList == null)
                    {
                        continue;
                    }

                    foreach (var codecItem in codecList)
                    {
                        var baseUrl = codecItem["base_url"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(baseUrl))
                        {
                            continue;
                        }

                        var urlList = codecItem["url_info"] as JArray;
                        if (urlList == null)
                        {
                            continue;
                        }

                        foreach (var urlItem in urlList)
                        {
                            var host = urlItem["host"]?.ToString() ?? string.Empty;
                            var extra = urlItem["extra"]?.ToString() ?? string.Empty;
                            var url = $"{host}{baseUrl}{extra}";
                            if (string.IsNullOrWhiteSpace(url) || !dedupe.Add(url))
                            {
                                continue;
                            }

                            var lowerProtocol = protocolName.ToLowerInvariant();
                            var lowerFormat = formatName.ToLowerInvariant();
                            var isFlv = lowerFormat == "flv" || url.IndexOf(".flv", StringComparison.OrdinalIgnoreCase) >= 0;
                            var isHls = lowerProtocol.Contains("hls")
                                || lowerFormat == "ts"
                                || lowerFormat == "fmp4"
                                || lowerFormat == "mp4"
                                || lowerFormat == "m4s"
                                || lowerFormat == "m3u8"
                                || url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

                            result.Add(new BilibiliPlayUrlCandidate()
                            {
                                Url = url,
                                IsFlv = isFlv,
                                IsHls = isHls,
                            });
                        }
                    }
                }
            }

            return result;
        }

        private async Task<List<string>> VerifyBilibiliHlsCandidatesAsync(HttpClient client, string roomID, IEnumerable<string> urls)
        {
            var verified = new List<string>();
            foreach (var url in urls.Where(x => !string.IsNullOrWhiteSpace(x)).Take(4))
            {
                try
                {
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            verified.Add(url);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoreDebug.Log($"[Bilibili] HLS探测失败 roomId={roomID} url={url} err={ex.GetType().FullName} {ex.Message}");
                }
            }
            return verified;
        }

        private static void AppendUniqueUrls(List<string> target, IEnumerable<string> urls)
        {
            if (target == null || urls == null)
            {
                return;
            }

            var seen = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);
            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                {
                    continue;
                }
                target.Add(url);
            }
        }

        private static bool IsPreferredBilibiliHlsHost(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return url.IndexOf("d1--cn", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return uri.Host.IndexOf("d1--cn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int? TryConvertToInt(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (int.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static List<string> OrderBilibiliPlayUrls(List<string> urls)
        {
            if (urls == null || urls.Count == 0)
            {
                return urls ?? new List<string>();
            }

            return urls
                .Select((url, index) => new
                {
                    url,
                    index,
                    mcdnPenalty = IsBilibiliMcdn(url) ? 1 : 0,
                    order = GetBilibiliUrlOrder(url),
                    score = GetBilibiliUrlScore(url)
                })
                .OrderBy(x => x.mcdnPenalty)
                .ThenBy(x => x.order)
                .ThenByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Select(x => x.url)
                .ToList();
        }

        private static bool IsBilibiliMcdn(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return url.IndexOf("mcdn", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var host = uri.Host ?? string.Empty;
            return host.IndexOf("mcdn", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetBilibiliUrlOrder(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return int.MaxValue;
            }

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return int.MaxValue;
                }

                var query = HttpUtility.ParseQueryString(uri.Query);
                if (int.TryParse(query["order"], out var order) && order > 0)
                {
                    return order;
                }
            }
            catch
            {
            }

            return int.MaxValue;
        }

        private static int GetBilibiliUrlScore(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return int.MinValue;
            }

            var score = 0;
            var host = string.Empty;
            var lower = url.ToLowerInvariant();

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = uri.Host ?? string.Empty;
            }

            if (lower.Contains(".flv"))
            {
                score += 2000;
            }
            else if (lower.Contains(".m3u8"))
            {
                score += 1000;
            }

            if (!string.IsNullOrEmpty(host) && host.IndexOf("mcdn", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score -= 200;
            }

            if (lower.StartsWith("https://"))
            {
                score += 10;
            }

            return score;
        }

        public async Task<bool> GetLiveStatus(object roomId)
        {
            var resp = await HttpUtil.GetString($"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}", headers: await GetRequestHeader());
            var obj = JObject.Parse(resp);
            return obj["data"]["live_status"].ToObject<int>() == 1;
        }

        public async Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId)
        {

            var resp = await HttpUtil.GetString($"https://api.live.bilibili.com/av/v1/SuperChat/getMessageList?room_id={roomId}", headers: await GetRequestHeader());
            var obj = JObject.Parse(resp);
            List<LiveSuperChatMessage> list = new List<LiveSuperChatMessage>();
            if (obj["data"]["list"].Type == JTokenType.Array)
            {
                foreach (var item in obj["data"]["list"])
                {
                    list.Add(new LiveSuperChatMessage()
                    {
                        BackgroundBottomColor = item["background_bottom_color"].ToString(),
                        BackgroundColor = item["background_color"].ToString(),
                        EndTime = Utils.TimestampToDateTime(item["end_time"].ToInt64()),
                        StartTime = Utils.TimestampToDateTime(item["start_time"].ToInt64()),
                        Face = $"{item["user_info"]["face"]}@200w.jpg",
                        Message = item["message"].ToString(),
                        Price = item["price"].ToInt32(),
                        UserName = item["user_info"]["uname"].ToString(),
                    });
                }
            }

            return list;
        }

        private string _accessId;
        private string _imgKey;
        private string _subKey;
        private int[] mixinKeyEncTab = new int[] {
            46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
            33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
            61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
            36, 20, 34, 44, 52
        };
        private async Task<(string, string)> GetWbiKeys()
        {
            if (_imgKey != null && _subKey != null)
            {
                return (_imgKey, _subKey);
            }
            // 获取最新的 img_key 和 sub_key
            var response = await HttpUtil.GetString(
                "https://api.bilibili.com/x/web-interface/nav",
                headers: await GetRequestHeader());
            var obj = JObject.Parse(response);

            var imgUrl = obj["data"]["wbi_img"]["img_url"].ToString();
            var subUrl = obj["data"]["wbi_img"]["sub_url"].ToString();
            var imgKey = imgUrl.Substring(imgUrl.LastIndexOf('/') + 1).Split('.')[0];
            var subKey = subUrl.Substring(subUrl.LastIndexOf('/') + 1).Split('.')[0];

            _imgKey = imgKey;
            _subKey = subKey;

            return (imgKey, subKey);
        }

        private string GetMixinKey(string origin)
        {
            // 对 imgKey 和 subKey 进行字符顺序打乱编码
            return mixinKeyEncTab.Aggregate("", (s, i) => s + origin[i]).Substring(0, 32);
        }

        public async Task<string> GetWbiSign(string url)
        {
            var (imgKey, subKey) = await GetWbiKeys();

            // 为请求参数进行 wbi 签名
            var mixinKey = GetMixinKey(imgKey + subKey);
            var currentTime = (long)Math.Round(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds);

            var queryString = HttpUtility.ParseQueryString(url);

            var queryParams = queryString.Cast<string>().ToDictionary(k => k, v => queryString[v]);
            queryParams["wts"] = currentTime + ""; // 添加 wts 字段
            queryParams = queryParams.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value); // 按照 key 重排参数
                                                                                                  // 过滤 value 中的 "!'()*" 字符
            queryParams = queryParams.ToDictionary(x => x.Key, x => string.Join("", x.Value.ToString().Where(c => "!'()*".Contains(c) == false)));

            var query = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

            var wbi_sign = Utils.ToMD5($"{query}{mixinKey}");

            return $"{query}&w_rid={wbi_sign}";
        }

        private async Task<(string, string)> GetBuvid()
        {
            try
            {
                var result = await HttpUtil.GetString($"https://api.bilibili.com/x/frontend/finger/spi",
                    headers: string.IsNullOrEmpty(Cookie) ? null : new Dictionary<string, string>
                    {
                        { "cookie", Cookie }
                    }
                  );
                var obj = JObject.Parse(result);

                return (obj["data"]["b_3"].ToString(), obj["data"]["b_4"].ToString());
            }
            catch (Exception)
            {
                return ("", "");
            }
        }

        private async Task<string> GetAssessId()
        {
            if (!string.IsNullOrEmpty(_accessId))
            {
                return _accessId;
            }

            var response = await HttpUtil.GetString(
                "https://live.bilibili.com/lol",
                headers: await GetRequestHeader());
            // 通过正则表达式"access_id":"(.*?)"提取
            var match = Regex.Match(response, "\"access_id\":\"(.*?)\"");
            if (match.Success)
            {
                _accessId = match.Groups[1].Value;
                return _accessId;
            }
            else
            {
                throw new Exception("无法获取 access_id");
            }
        }
    }


}
