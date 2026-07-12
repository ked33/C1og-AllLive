using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
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
using System.IO;
using System.IO.Compression;
using System.Text;

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
            var isLive = roomInfo["live_status"].ToInt32() == 1;
            var actualRoomId = roomInfo["room_id"].ToInt32();
            long? viewerCount = null;
            long? popularity = null;
            string popularitySource = null;
            var allowPopularityFallback = false;
            if (isLive)
            {
                // 严格首帧优先：房间详情不再创建临时 WebSocket 等待真实人数。
                // 首次进入 Playing 后启动正式弹幕连接，由其 ONLINE_RANK_COUNT
                // 消息更新 ViewerCount，同时继续保持 B 站热度显示禁用。
                CoreDebug.Log(() => $"[Bilibili] 严格首帧优先 roomId={actualRoomId} 跳过首播前真实人数等待，Playing后由正式弹幕更新");
            }
            else
            {
                CoreDebug.Log(() => $"[Bilibili] 房间详情人数 roomId={roomId} 未开播，跳过初始观众数和热度");
            }

            var compatibleOnline = viewerCount ?? popularity ?? 0L;

            return new LiveRoomDetail()
            {
                Cover = roomInfo["cover"].ToString(),
                Online = ToCompatibleOnline(compatibleOnline),
                Popularity = popularity,
                ViewerCount = viewerCount,
                PopularitySource = popularitySource,
                ViewerCountSource = viewerCount.HasValue ? "websocket.ONLINE_RANK_COUNT" : null,
                AllowPopularityFallback = allowPopularityFallback,
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

        private async Task<InitialViewerCountResult> GetInitialViewerCount(int roomId)
        {
            if (roomId <= 0)
            {
                return InitialViewerCountResult.Failed("roomId无效");
            }

            try
            {
                var danmuInfo = await GetDanmuInfo(roomId);
                var token = danmuInfo?["token"]?.ToString();
                var host = (danmuInfo?["host_list"] as JArray)?.LastOrDefault()?["host"]?.ToString();
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(host))
                {
                    CoreDebug.Log(() => $"[Bilibili] 初始房间观众数跳过 roomId={roomId} token/host为空");
                    return InitialViewerCountResult.Failed("token/host为空");
                }

                var resultSource = new TaskCompletionSource<InitialViewerCountResult>();
                var ws = new WebSocket($"wss://{host}/sub");
                if (!string.IsNullOrEmpty(Cookie))
                {
                    ws.CustomHeaders = new Dictionary<string, string>()
                    {
                        { "Cookie", GetCookie() },
                    };
                }
                ws.Compression = CompressionMethod.Deflate;

                EventHandler onOpen = null;
                EventHandler<MessageEventArgs> onMessage = null;
                EventHandler<WebSocketSharp.ErrorEventArgs> onError = null;
                EventHandler<CloseEventArgs> onClose = null;

                onOpen = (sender, args) =>
                {
                    try
                    {
                        ws.Send(EncodeBilibiliWsData(JsonConvert.SerializeObject(new
                        {
                            roomid = roomId,
                            uid = UserId,
                            protover = 2,
                            key = token,
                            platform = "web",
                            type = 2,
                            buvid = buvid3,
                        }), 7));
                    }
                    catch (Exception ex)
                    {
                        CoreDebug.Log(() => $"[Bilibili] 初始房间观众数进房发送失败 roomId={roomId} err={ex.GetType().FullName} {ex.Message}");
                        resultSource.TrySetResult(InitialViewerCountResult.Failed("进房发送失败"));
                    }
                };

                onMessage = (sender, args) =>
                {
                    try
                    {
                        if (TryExtractOnlineRankCount(args.RawData, out var count))
                        {
                            resultSource.TrySetResult(InitialViewerCountResult.Success(count));
                        }
                    }
                    catch (Exception ex)
                    {
                        CoreDebug.Log(() => $"[Bilibili] 初始房间观众数解析失败 roomId={roomId} err={ex.GetType().FullName} {ex.Message}");
                    }
                };

                onError = (sender, args) =>
                {
                    CoreDebug.Log(() => $"[Bilibili] 初始房间观众数WS错误 roomId={roomId} err={args.Message}");
                    resultSource.TrySetResult(InitialViewerCountResult.Failed("WS错误"));
                };

                onClose = (sender, args) =>
                {
                    if (args.Code != 1000)
                    {
                        CoreDebug.Log(() => $"[Bilibili] 初始房间观众数WS关闭 roomId={roomId} code={args.Code} reason={args.Reason}");
                    }
                    resultSource.TrySetResult(InitialViewerCountResult.Failed($"WS关闭:{args.Code}"));
                };

                ws.OnOpen += onOpen;
                ws.OnMessage += onMessage;
                ws.OnError += onError;
                ws.OnClose += onClose;

                try
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            ws.Connect();
                        }
                        catch (Exception ex)
                        {
                            CoreDebug.Log(() => $"[Bilibili] 初始房间观众数WS连接失败 roomId={roomId} err={ex.GetType().FullName} {ex.Message}");
                            resultSource.TrySetResult(InitialViewerCountResult.Failed("WS连接失败"));
                        }
                    });

                    var completed = await Task.WhenAny(resultSource.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                    if (completed == resultSource.Task)
                    {
                        return await resultSource.Task;
                    }

                    CoreDebug.Log(() => $"[Bilibili] 初始房间观众数等待超时 roomId={roomId}");
                    return InitialViewerCountResult.Pending("等待超时");
                }
                finally
                {
                    ws.OnOpen -= onOpen;
                    ws.OnMessage -= onMessage;
                    ws.OnError -= onError;
                    ws.OnClose -= onClose;
                    try { ws.Close(); } catch { }
                }
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Bilibili] 初始房间观众数获取失败 roomId={roomId} err={ex.GetType().FullName} {ex.Message}");
                return InitialViewerCountResult.Failed("获取失败");
            }
        }

        private sealed class InitialViewerCountResult
        {
            public long? Count { get; private set; }
            public bool AllowPopularityFallback { get; private set; }
            public string FailureReason { get; private set; }

            public static InitialViewerCountResult Success(long count)
            {
                return new InitialViewerCountResult()
                {
                    Count = count,
                    AllowPopularityFallback = false
                };
            }

            public static InitialViewerCountResult Pending(string reason)
            {
                return new InitialViewerCountResult()
                {
                    AllowPopularityFallback = false,
                    FailureReason = reason
                };
            }

            public static InitialViewerCountResult Failed(string reason)
            {
                return new InitialViewerCountResult()
                {
                    AllowPopularityFallback = true,
                    FailureReason = reason
                };
            }
        }

        private async Task<JToken> GetDanmuInfo(int roomId)
        {
            var url = "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo";
            var query = $"id={roomId}&type=0&web_location=444.8";
            query = await GetWbiSign(query);
            var result = await HttpUtil.GetString($"{url}?{query}", headers: await GetRequestHeader());
            var obj = JObject.Parse(result);
            if (obj["code"]?.ToInt32() != 0)
            {
                CoreDebug.Log(() => $"[Bilibili] getDanmuInfo返回异常 roomId={roomId} code={obj["code"]} message={obj["message"] ?? obj["msg"]}");
                return null;
            }
            return obj["data"];
        }

        private static bool TryExtractOnlineRankCount(byte[] data, out long count)
        {
            count = 0;
            if (data == null || data.Length < 16)
            {
                return false;
            }

            var offset = 0;
            while (offset + 16 <= data.Length)
            {
                var packetLength = ReadInt32BigEndian(data, offset);
                if (packetLength < 16 || offset + packetLength > data.Length)
                {
                    break;
                }

                var protocolVersion = ReadInt16BigEndian(data, offset + 6);
                var operation = ReadInt32BigEndian(data, offset + 8);
                var bodyLength = packetLength - 16;
                var body = new byte[bodyLength];
                Buffer.BlockCopy(data, offset + 16, body, 0, bodyLength);

                if (operation == 5)
                {
                    if (protocolVersion == 2)
                    {
                        var decompressed = DecompressBilibiliDeflate(body);
                        if (TryExtractOnlineRankCount(decompressed, out count) || TryParseOnlineRankText(decompressed, out count))
                        {
                            return true;
                        }
                    }
                    else if (TryParseOnlineRankText(body, out count))
                    {
                        return true;
                    }
                }

                offset += packetLength;
            }

            if (offset == 0)
            {
                return TryParseOnlineRankText(data, out count);
            }
            return false;
        }

        private static bool TryParseOnlineRankText(byte[] data, out long count)
        {
            count = 0;
            var text = Encoding.UTF8.GetString(data);
            var textLines = Regex.Split(text, "[\x00-\x1f]+").Where(x => x.Length > 2 && x[0] == '{').ToArray();
            foreach (var item in textLines)
            {
                try
                {
                    var obj = JObject.Parse(item);
                    var cmd = obj["cmd"]?.ToString() ?? "";
                    if (cmd != "ONLINE_RANK_COUNT")
                    {
                        continue;
                    }
                    var viewerCount = obj["data"]?["online_count_text"].ParseCountTextToLong()
                        ?? obj["data"]?["count_text"].ParseCountTextToLong()
                        ?? obj["data"]?["online_count"].ParseCountTextToLong()
                        ?? obj["data"]?["count"].ParseCountTextToLong();
                    if (viewerCount.HasValue)
                    {
                        count = viewerCount.Value;
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static byte[] EncodeBilibiliWsData(string msg, int action)
        {
            var data = Encoding.UTF8.GetBytes(msg);
            var buffer = new byte[data.Length + 16];
            using (var ms = new MemoryStream(buffer))
            {
                WriteBigEndian(ms, buffer.Length, 4);
                WriteBigEndian(ms, 16, 2);
                WriteBigEndian(ms, 0, 2);
                WriteBigEndian(ms, action, 4);
                WriteBigEndian(ms, 1, 4);
                ms.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        private static byte[] DecompressBilibiliDeflate(byte[] data)
        {
            using (var outBuffer = new MemoryStream())
            using (var compressedStream = new DeflateStream(new MemoryStream(data, 2, data.Length - 2), CompressionMode.Decompress))
            {
                var block = new byte[1024];
                while (true)
                {
                    var bytesRead = compressedStream.Read(block, 0, block.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }
                    outBuffer.Write(block, 0, bytesRead);
                }
                return outBuffer.ToArray();
            }
        }

        private static int ReadInt32BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24)
                | (data[offset + 1] << 16)
                | (data[offset + 2] << 8)
                | data[offset + 3];
        }

        private static int ReadInt16BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static void WriteBigEndian(MemoryStream ms, int value, int byteCount)
        {
            for (var i = byteCount - 1; i >= 0; i--)
            {
                ms.WriteByte((byte)((value >> (i * 8)) & 0xFF));
            }
        }

        private static int ToCompatibleOnline(long value)
        {
            if (value <= 0)
            {
                return 0;
            }
            if (value > int.MaxValue)
            {
                return int.MaxValue;
            }
            return (int)value;
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
                CoreDebug.Log(() => $"[Bilibili] GetPlayQualityNew返回空列表 roomId={roomDetail?.RoomID}，回退旧接口");
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Bilibili] GetPlayQualityNew失败 roomId={roomDetail?.RoomID} err={ex.GetType().FullName} {ex.Message}，回退旧接口");
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
                var playurl = (await RequestBilibiliPlayInfoAsync(client, roomID, null, "0"))?["data"]?["playurl_info"]?["playurl"];
                var qualitiesMap = new Dictionary<int, string>();
                foreach (var item in playurl?["g_qn_desc"] ?? new JArray())
                {
                    qualitiesMap[item["qn"].ToObject<int>()] = item["desc"]?.ToString() ?? "未知清晰度";
                }
                var acceptQnList = GetBilibiliAcceptQnList(playurl);
                if (acceptQnList.Count == 0)
                {
                    CoreDebug.Log(() => $"[Bilibili] AVC清晰度为空，尝试HEVC兜底 roomId={roomID}");
                    playurl = (await RequestBilibiliPlayInfoAsync(client, roomID, null, "0,1"))?["data"]?["playurl_info"]?["playurl"];
                    qualitiesMap.Clear();
                    foreach (var item in playurl?["g_qn_desc"] ?? new JArray())
                    {
                        qualitiesMap[item["qn"].ToObject<int>()] = item["desc"]?.ToString() ?? "未知清晰度";
                    }
                    acceptQnList = GetBilibiliAcceptQnList(playurl);
                }
                if (acceptQnList.Count == 0)
                {
                    acceptQnList.AddRange(qualitiesMap.Keys);
                }
                foreach (var qnValue in acceptQnList)
                {
                    var qualityText = qualitiesMap.ContainsKey(qnValue) ? qualitiesMap[qnValue] : "未知清晰度";
                    var qualityItem = new LivePlayQuality()
                    {
                        Quality = qualityText,
                        Data = qnValue,
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
            var qualityDescription = obj["data"]?["quality_description"] as JArray;
            if (qualityDescription == null)
            {
                return qualities;
            }
            foreach (var item in qualityDescription)
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
                CoreDebug.Log(() => $"[Bilibili] GetPlayUrlsNew返回空列表 roomId={roomDetail?.RoomID} qn={qn?.Data}，回退旧接口");
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Bilibili] GetPlayUrlsNew失败 roomId={roomDetail?.RoomID} qn={qn?.Data} err={ex.GetType().FullName} {ex.Message}，回退旧接口");
            }
            return await GetPlayUrlsOld(roomDetail.RoomID, qn.Data);
        }
        private async Task<List<string>> GetPlayUrlsNew(string roomID, object qn)
        {
            var qnValue = TryConvertToInt(qn);

            var client = await CreateBilibiliPlayInfoHttpClientAsync();
            using (client)
            {
                var obj = await RequestBilibiliPlayInfoAsync(client, roomID, qnValue, "0");
                var playurl = obj?["data"]?["playurl_info"]?["playurl"];
                var candidates = ParseBilibiliPlayUrlCandidates(playurl);
                LogBilibiliCandidateSummary(roomID, qnValue, candidates, "AVC");
                if (candidates.Count == 0)
                {
                    CoreDebug.Log(() => $"[Bilibili] AVC播放流为空，尝试HEVC兜底 roomId={roomID} qn={qnValue?.ToString() ?? "null"}");
                    obj = await RequestBilibiliPlayInfoAsync(client, roomID, qnValue, "0,1");
                    playurl = obj?["data"]?["playurl_info"]?["playurl"];
                    candidates = ParseBilibiliPlayUrlCandidates(playurl);
                    LogBilibiliCandidateSummary(roomID, qnValue, candidates, "AVC+HEVC");
                }

                var selectedCandidates = await SelectBilibiliPlayUrlCandidatesAsync(client, roomID, qnValue, candidates);
                var urls = await BuildBilibiliPlayUrlsWithLocalProxyFallbackAsync(roomID, qnValue, selectedCandidates);
                CoreDebug.Log(() => $"[Bilibili] PlayInfo直出 roomId={roomID} qn={qnValue?.ToString() ?? "null"} total={urls.Count} flv={selectedCandidates.Count(x => x.IsFlv)} hls={selectedCandidates.Count(x => x.IsHls)} hevc={selectedCandidates.Count(x => x.IsHevc)}");
                return urls;
            }
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
            var durl = obj["data"]?["durl"] as JArray;
            if (durl == null)
            {
                return urls;
            }
            foreach (var item in durl)
            {
                urls.Add(item["url"].ToString());
            }
            return OrderBilibiliPlayUrls(urls);
        }

        private sealed class BilibiliPlayUrlCandidate
        {
            public string Url { get; set; }
            public string ProtocolName { get; set; }
            public string FormatName { get; set; }
            public string CodecName { get; set; }
            public bool IsFlv { get; set; }
            public bool IsHls { get; set; }
            public bool IsHevc { get; set; }
            public bool IsMcdn { get; set; }
            public int Order { get; set; }
            public int Score { get; set; }
            /// <summary>探测已确认可连通（HTTP 2xx）</summary>
            public bool Verified { get; set; }
            /// <summary>探测已确认不可用（HTTP 非成功状态）</summary>
            public bool VerifyFailed { get; set; }
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

        private async Task<JObject> RequestBilibiliPlayInfoAsync(HttpClient client, string roomID, int? qn, string codec)
        {
            var queryParameters = new Dictionary<string, string>()
            {
                { "room_id", roomID },
                { "protocol", "0,1" },
                { "format", "0,1,2" },
                { "codec", string.IsNullOrWhiteSpace(codec) ? "0" : codec },
                { "platform", "html5" },
                { "dolby", "5" },
            };
            if (qn.HasValue)
            {
                queryParameters["qn"] = qn.Value.ToString();
            }
            var query = string.Join("&", queryParameters.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value ?? string.Empty)}"));
            var requestUrl = $"https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo?{query}";
            using (var response = await client.GetAsync(requestUrl))
            {
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadAsStringAsync();
                var obj = JObject.Parse(result);
                if (obj["code"]?.ToObject<int>() != 0)
                {
                    throw new Exception($"getRoomPlayInfo返回异常 code={obj["code"]} message={obj["message"] ?? obj["msg"]}");
                }
                return obj;
            }
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
                        var codecName = codecItem["codec_name"]?.ToString() ?? string.Empty;
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
                                ProtocolName = protocolName,
                                FormatName = formatName,
                                CodecName = codecName,
                                IsFlv = isFlv,
                                IsHls = isHls,
                                IsHevc = codecName.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0,
                                IsMcdn = IsBilibiliMcdn(url),
                                Order = GetBilibiliUrlOrder(url),
                                Score = GetBilibiliUrlScore(url),
                            });
                        }
                    }
                }
            }

            return result;
        }

        private async Task<List<BilibiliPlayUrlCandidate>> SelectBilibiliPlayUrlCandidatesAsync(HttpClient client, string roomID, int? qnValue, List<BilibiliPlayUrlCandidate> candidates)
        {
            var all = candidates ?? new List<BilibiliPlayUrlCandidate>();
            var flvCandidates = all.Where(x => x != null && x.IsFlv).ToList();
            if (flvCandidates.Count > 0)
            {
                // 官方 FLV 直连优先；本地代理仅作为需要 Cookie/桌面网络栈时的兼容回退。
                var orderedFlv = OrderBilibiliPlayUrlCandidates(all);
                CoreDebug.Log(() => $"[Bilibili] 选择官方FLV直连优先 roomId={roomID} qn={qnValue?.ToString() ?? "null"} flv={flvCandidates.Count} first={BuildBilibiliUrlBrief(orderedFlv[0])}");
                return orderedFlv;
            }

            var hlsCandidates = all.Where(x => x != null && x.IsHls).ToList();
            if (hlsCandidates.Count == 0)
            {
                CoreDebug.Log(() => $"[Bilibili] 未获得FLV或HLS候选 roomId={roomID} qn={qnValue?.ToString() ?? "null"} total={all.Count}");
                return OrderBilibiliPlayUrlCandidates(all);
            }

            // 对全部 HLS 候选（含非 d1--cn 线路）并发探测可连通性，把结果写回候选，
            // 再由统一排序键（明确 HTTP 失败垫底 → 编码偏好 → 探测成功优先）决定最终顺序，
            // 避免出现“探测认可 A、实际却播未探测的 B”的选路脱节。
            CoreDebug.Log(() => $"[Bilibili] 未获得FLV，探测全部HLS候选 roomId={roomID} qn={qnValue?.ToString() ?? "null"} hls={hlsCandidates.Count}");
            var verified = await VerifyBilibiliHlsCandidatesAsync(client, roomID, hlsCandidates);
            var ordered = OrderBilibiliPlayUrlCandidates(all);
            var first = ordered.FirstOrDefault();
            CoreDebug.Log(() => $"[Bilibili] HLS探测完成 roomId={roomID} qn={qnValue?.ToString() ?? "null"} verified={verified.Count}/{hlsCandidates.Count} first={(first != null ? BuildBilibiliUrlBrief(first) : "无")}");
            return ordered;
        }

        private async Task<List<string>> BuildBilibiliPlayUrlsWithLocalProxyFallbackAsync(string roomID, int? qnValue, List<BilibiliPlayUrlCandidate> selectedCandidates)
        {
            var urls = new List<string>();
            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var firstFlv = selectedCandidates?.FirstOrDefault(x => x != null && x.IsFlv && !string.IsNullOrWhiteSpace(x.Url));

            // 先返回全部官方 FLV 直连候选，避免本地代理故障阻塞首帧。
            foreach (var candidate in selectedCandidates?.Where(x => x != null && x.IsFlv)
                ?? Enumerable.Empty<BilibiliPlayUrlCandidate>())
            {
                if (!string.IsNullOrWhiteSpace(candidate.Url) && dedupe.Add(candidate.Url))
                {
                    urls.Add(candidate.Url);
                }
            }

            if (firstFlv != null)
            {
                var headers = await GetRequestHeader();
                var proxyResult = await BilibiliLocalFlvProxyClient.RegisterAsync(firstFlv.Url, headers);
                if (proxyResult.Success && !string.IsNullOrWhiteSpace(proxyResult.Url))
                {
                    if (dedupe.Add(proxyResult.Url))
                    {
                        urls.Add(proxyResult.Url);
                    }
                    CoreDebug.Log(() => $"[Bilibili] 注册本地FLV代理回退 roomId={roomID} qn={qnValue?.ToString() ?? "null"} upstream={BuildBilibiliUrlBrief(firstFlv)} proxy={BuildBilibiliLocalProxyBrief(proxyResult.Url)}");
                }
                else
                {
                    CoreDebug.Log(() => $"[Bilibili] 本地FLV代理回退不可用，继续使用官方直连 roomId={roomID} qn={qnValue?.ToString() ?? "null"} upstream={BuildBilibiliUrlBrief(firstFlv)} err={proxyResult.Error}");
                }
            }
            else
            {
                CoreDebug.Log(() => $"[Bilibili] 未获得FLV，无法使用本地代理 roomId={roomID} qn={qnValue?.ToString() ?? "null"} total={selectedCandidates?.Count ?? 0}");
            }

            // 代理回退之后再追加 HLS/其他协议候选。
            foreach (var candidate in selectedCandidates?.Where(x => x != null && !x.IsFlv)
                ?? Enumerable.Empty<BilibiliPlayUrlCandidate>())
            {
                if (string.IsNullOrWhiteSpace(candidate?.Url))
                {
                    continue;
                }

                if (dedupe.Add(candidate.Url))
                {
                    urls.Add(candidate.Url);
                }
            }

            return urls;
        }

        private static void LogBilibiliCandidateSummary(string roomID, int? qnValue, List<BilibiliPlayUrlCandidate> candidates, string stage)
        {
            var total = candidates?.Count ?? 0;
            var flv = candidates?.Count(x => x.IsFlv) ?? 0;
            var hls = candidates?.Count(x => x.IsHls) ?? 0;
            var hevc = candidates?.Count(x => x.IsHevc) ?? 0;
            CoreDebug.Log(() => $"[Bilibili] PlayInfo候选 roomId={roomID} qn={qnValue?.ToString() ?? "null"} stage={stage} total={total} flv={flv} hls={hls} hevc={hevc}");
        }

        private static List<int> GetBilibiliAcceptQnList(JToken playurl)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            var streamList = playurl?["stream"] as JArray;
            if (streamList == null)
            {
                return result;
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
                        var acceptQn = codecItem["accept_qn"] as JArray;
                        if (acceptQn == null)
                        {
                            continue;
                        }

                        foreach (var item in acceptQn)
                        {
                            var qnValue = TryConvertToInt(item);
                            if (qnValue.HasValue && seen.Add(qnValue.Value))
                            {
                                result.Add(qnValue.Value);
                            }
                        }
                    }
                }
            }

            return result;
        }

        private async Task<List<BilibiliPlayUrlCandidate>> VerifyBilibiliHlsCandidatesAsync(HttpClient client, string roomID, IEnumerable<BilibiliPlayUrlCandidate> candidates)
        {
            var targets = (candidates ?? Enumerable.Empty<BilibiliPlayUrlCandidate>())
                .Where(x => !string.IsNullOrWhiteSpace(x?.Url))
                .ToList();
            if (targets.Count == 0)
            {
                return new List<BilibiliPlayUrlCandidate>();
            }

            // 并发探测，每条独立 2.5s 超时，避免串行长超时（此前每条卡满 5s、总耗时可达十几秒）。
            var tasks = targets.Select(async candidate =>
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500)))
                {
                    try
                    {
                        using (var response = await client.GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                candidate.Verified = true;
                            }
                            else
                            {
                                candidate.VerifyFailed = true;
                                CoreDebug.Log(() => $"[Bilibili] HLS探测返回异常 roomId={roomID} status={(int)response.StatusCode} url={BuildBilibiliUrlBrief(candidate)}");
                            }
                        }
                    }
                    catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
                    {
                        // 2.5 秒只能作为首选线路的快速提示，超时不等于不可播放。
                        // FFmpeg 完整建源还会继续读取子播放列表、初始化分片和媒体分片，
                        // 某些线路可能在探测窗口后才响应，但仍能正常播放。
                        CoreDebug.Log(() => $"[Bilibili] HLS探测未确认 roomId={roomID} url={BuildBilibiliUrlBrief(candidate)} reason=timeout err={ex.GetType().FullName} {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        // 网络异常可能是瞬时状态，也不能据此永久压低该候选；保持未确认，
                        // 只有明确的 HTTP 非成功状态才标记 VerifyFailed。
                        CoreDebug.Log(() => $"[Bilibili] HLS探测未确认 roomId={roomID} url={BuildBilibiliUrlBrief(candidate)} reason=exception err={ex.GetType().FullName} {ex.Message}");
                    }
                }
            });

            await Task.WhenAll(tasks);
            return targets.Where(x => x.Verified).ToList();
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
                    flvPenalty = IsBilibiliFlvUrl(url) ? 0 : 1,
                    mcdnPenalty = IsBilibiliMcdn(url) ? 1 : 0,
                    order = GetBilibiliUrlOrder(url),
                    score = GetBilibiliUrlScore(url)
                })
                .OrderBy(x => x.flvPenalty)
                .ThenBy(x => x.mcdnPenalty)
                .ThenBy(x => x.order)
                .ThenByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Select(x => x.url)
                .ToList();
        }

        private static List<BilibiliPlayUrlCandidate> OrderBilibiliPlayUrlCandidates(List<BilibiliPlayUrlCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return candidates ?? new List<BilibiliPlayUrlCandidate>();
            }

            return candidates
                .Select((candidate, index) => new
                {
                    candidate,
                    index,
                    flvPenalty = candidate.IsFlv ? 0 : 1,
                    verifyFailedPenalty = candidate.VerifyFailed ? 1 : 0,
                    codecPenalty = GetBilibiliCodecPenalty(candidate.CodecName),
                    unverifiedPenalty = candidate.Verified ? 0 : 1,
                    preferredPenalty = IsPreferredBilibiliHlsHost(candidate.Url) ? 0 : 1,
                    mcdnPenalty = candidate.IsMcdn ? 1 : 0,
                    order = candidate.Order,
                    score = candidate.Score
                })
                .OrderBy(x => x.flvPenalty)
                .ThenBy(x => x.verifyFailedPenalty)
                .ThenBy(x => x.codecPenalty)
                .ThenBy(x => x.unverifiedPenalty)
                .ThenBy(x => x.preferredPenalty)
                .ThenBy(x => x.mcdnPenalty)
                .ThenBy(x => x.order)
                .ThenByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Select(x => x.candidate)
                .ToList();
        }

        private static bool IsBilibiliFlvUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) && url.IndexOf(".flv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetBilibiliCodecPenalty(string codecName)
        {
            if (string.IsNullOrWhiteSpace(codecName))
            {
                return 3;
            }

            if (codecName.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 0;
            }
            if (codecName.IndexOf("hevc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            if (codecName.IndexOf("av1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 2;
            }
            return 3;
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

        private static string BuildBilibiliUrlBrief(BilibiliPlayUrlCandidate candidate)
        {
            if (candidate == null)
            {
                return "null";
            }

            var url = candidate.Url ?? string.Empty;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return $"{uri.Host}{uri.AbsolutePath} format={candidate.FormatName} protocol={candidate.ProtocolName} codec={candidate.CodecName} order={candidate.Order} score={candidate.Score}";
            }

            return $"len={url.Length} format={candidate.FormatName} protocol={candidate.ProtocolName} codec={candidate.CodecName} order={candidate.Order} score={candidate.Score}";
        }

        private static string BuildBilibiliLocalProxyBrief(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return $"{uri.Host}:{uri.Port}{uri.AbsolutePath}";
            }

            return $"len={url?.Length ?? 0}";
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
