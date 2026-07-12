using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AllLive.Core.Danmaku;
using AllLive.Core.Helper;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Web;
using WebSocketSharp;
using System.Collections.Specialized;
using static System.Net.Mime.MediaTypeNames;
using System.IO;
using AllLive.Core.Models.Tars;

namespace AllLive.Core
{
    public class Huya : ILiveSite, IDeferredAudienceMetricSite
    {
        public string Name => "虎牙直播";
        public ILiveDanmaku GetDanmaku() => new HuyaDanmaku();
        TupHttpHelper tupHttpHelper = new TupHttpHelper("http://wup.huya.com", "liveui");
        TupHttpHelper wupuiTupHttpHelper = new TupHttpHelper("http://wup.huya.com", "wupui");
        public async Task<List<LiveCategory>> GetCategores()
        {
            List<LiveCategory> categories = new List<LiveCategory>() {
                new LiveCategory() {
                    ID="1",
                    Name="网游",
                },
                new LiveCategory() {
                    ID="2",
                    Name="单机",
                },
                new LiveCategory() {
                    ID="8",
                    Name="娱乐",
                },
                new LiveCategory() {
                    ID="3",
                    Name="手游",
                },
            };
            foreach (var item in categories)
            {
                item.Children = await GetSubCategories(item.ID);
            }
            return categories;
        }

        private async Task<List<LiveSubCategory>> GetSubCategories(string id)
        {
            List<LiveSubCategory> subs = new List<LiveSubCategory>();
            var result = await HttpUtil.GetString($"https://live.cdn.huya.com/liveconfig/game/bussLive?bussType={id}");
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"])
            {
                subs.Add(new LiveSubCategory()
                {
                    Pic = $"https://huyaimg.msstatic.com/cdnimage/game/{item["gid"].ToString()}-MS.jpg",
                    ID = item["gid"].ToString(),
                    ParentID = id,
                    Name = item["gameFullName"].ToString(),
                });
            }
            return subs;
        }
        public async Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.huya.com/cache.php?m=LiveList&do=getLiveListByPage&tagAll=0&gameId={category.ID}&page={page}");
            var obj = JObject.Parse(result);
            foreach (var item in obj["data"]["datas"])
            {
                var cover = item["screenshot"].ToString();
                if (!cover.Contains("?"))
                {
                    cover += "?x-oss-process=style/w338_h190&";
                }
                var title = item["introduction"]?.ToString();
                if (string.IsNullOrEmpty(title))
                {
                    title = item["roomName"]?.ToString() ?? "";
                }
                categoryResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = cover,
                    Online = item["totalCount"].ToInt32(),
                    RoomID = item["profileRoom"].ToString(),
                    Title = title,
                    UserName = item["nick"].ToString(),
                });
            }
            categoryResult.HasMore = obj["data"]["page"].ToInt32() < obj["data"]["totalPage"].ToInt32();
            return categoryResult;
        }
        public async Task<LiveCategoryResult> GetRecommendRooms(int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.huya.com/cache.php?m=LiveList&do=getLiveListByPage&tagAll=0&page={page}");
            var obj = JObject.Parse(result);

            foreach (var item in obj["data"]["datas"])
            {
                var cover = item["screenshot"].ToString();
                if (!cover.Contains("?"))
                {
                    cover += "?x-oss-process=style/w338_h190&";
                }
                var title = item["introduction"]?.ToString();
                if (string.IsNullOrEmpty(title))
                {
                    title = item["roomName"]?.ToString() ?? "";
                }
                categoryResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = cover,
                    Online = item["totalCount"].ToInt32(),
                    RoomID = item["profileRoom"].ToString(),
                    Title = title,
                    UserName = item["nick"].ToString(),
                });
            }
            categoryResult.HasMore = obj["data"]["page"].ToInt32() < obj["data"]["totalPage"].ToInt32();
            return categoryResult;
        }
        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            var jsonObj = await GetRoomInfo(roomId);
            var topSid = jsonObj["topSid"].ToInt64();
            var subSid = jsonObj["subSid"].ToInt64();

            var title = jsonObj["roomInfo"]["tLiveInfo"]["sIntroduction"].ToString();
            if (string.IsNullOrEmpty(title))
            {
                title = jsonObj["roomInfo"]["tLiveInfo"]["sRoomName"].ToString();
            }

            var uid = await GetUid();
            var uuid = GetUuid();
            CoreDebug.Log(() => $"[Huya] GetRoomDetail roomId={roomId} uid={uid} uuid={uuid}");
            var huyaLines = new List<HuyaLineModel>();
            var huyaBiterates = new List<HuyaBitRateModel>();
            //读取可用线路
            var lines = jsonObj["roomInfo"]["tLiveInfo"]["tLiveStreamInfo"]["vStreamInfo"]["value"];
            var lineIndex = 0;
            foreach (var item in lines)
            {
                if (!string.IsNullOrEmpty(item["sFlvUrl"]?.ToString()))
                {
                    var cdnType = item["sCdnType"]?.ToString();
                    huyaLines.Add(new HuyaLineModel()
                    {
                        Line = item["sFlvUrl"].ToString(),
                        LineType = HuyaLineType.FLV,
                        FlvAntiCode = item["sFlvAntiCode"].ToString(),
                        HlsAntiCode = item["sHlsAntiCode"].ToString(),
                        StreamName = item["sStreamName"].ToString(),
                        CdnType = cdnType,
                        PresenterUid = item["lChannelId"].ToInt64(),
                    });
                    if (lineIndex == 0 && item is JObject firstObj)
                    {
                        var keys = string.Join(",", firstObj.Properties().Select(p => p.Name));
                        CoreDebug.Log(() => $"[Huya] StreamInfo keys: {keys}");
                    }
                    CoreDebug.Log(() => $"[Huya] Line[{lineIndex}] url={item["sFlvUrl"]} stream={item["sStreamName"]} cdnType={cdnType}");
                    lineIndex++;
                }
                //HLS效果不好，暂不使用
                //if (!string.IsNullOrEmpty(item["sHlsUrl"]?.ToString()))
                //{
                //    huyaLines.Add(new HuyaLineModel()
                //    {
                //        Line = item["sHlsUrl"].ToString().Replace("http://", "").Replace("https://", ""),
                //        LineType = HuyaLineType.HLS,
                //    });
                //}
            }

            // 将AL的线路放到最后,AL的线路非常容易出现403
            huyaLines = huyaLines.OrderBy(x => x.Line.Contains("al.flv.")).ToList();

            //优先FLV
            //huyaLines=huyaLines.Where(x=>!x.Line.Contains("-game")).OrderBy(x=>x.LineType).ToList();

            //清晰度
            var biterates = jsonObj["roomInfo"]["tLiveInfo"]["tLiveStreamInfo"]["vBitRateInfo"]["value"];
            foreach (var item in biterates)
            {
                huyaBiterates.Add(new HuyaBitRateModel()
                {
                    BitRate = item["iBitRate"].ToInt32(),
                    Name = item["sDisplayName"].ToString(),
                });
            }
            CoreDebug.Log(() => $"[Huya] Lines={huyaLines.Count} BitRates={huyaBiterates.Count}");
            var realRoomId = jsonObj["roomInfo"]["tLiveInfo"]["lProfileRoom"].ToInt32();
            if (realRoomId == 0)
            {
                realRoomId = jsonObj["roomInfo"]["tProfileInfo"]["lProfileRoom"].ToInt32();
            }
            var isLive = jsonObj["roomInfo"]["eLiveStatus"].ToInt32() == 2;
            var popularity = jsonObj["roomInfo"]["tLiveInfo"]["lTotalCount"].ParseCountTextToLong() ?? 0;
            var presenterUid = TryGetPresenterUid(jsonObj["roomInfo"]);

            return new LiveRoomDetail()
            {
                Cover = jsonObj["roomInfo"]["tLiveInfo"]["sScreenshot"].ToString(),
                // 严格首帧优先：播放前不等待真实人数或贵宾人数，也不显示热度/0占位。
                // 首次 Playing 后由 GetDeferredAudienceMetrics 请求；全部失败后才允许回退热度。
                Online = 0,
                ViewerCount = null,
                ViewerCountSource = null,
                VipCount = null,
                VipCountSource = null,
                Popularity = popularity,
                PopularitySource = "m.huya.tLiveInfo.lTotalCount",
                AllowPopularityFallback = !isLive,
                RoomID = realRoomId.ToString(),
                Title = title,
                UserName = jsonObj["roomInfo"]["tProfileInfo"]["sNick"].ToString(),
                UserAvatar = jsonObj["roomInfo"]["tProfileInfo"]["sAvatar180"].ToString(),
                Introduction = jsonObj["roomInfo"]["tLiveInfo"]["sIntroduction"].ToString(),
                Notice = jsonObj["welcomeText"].ToString(),
                Status = isLive,
                Data = new HuyaUrlDataModel()
                {
                    Url = "https:" + Encoding.UTF8.GetString(Convert.FromBase64String(jsonObj["roomProfile"]["liveLineUrl"].ToString())),
                    Lines = huyaLines,
                    BitRates = huyaBiterates,
                    Uid = uid,
                    UUid = uuid,
                    PresenterUid = presenterUid,
                },
                DanmakuData = new HuyaDanmakuArgs(
                    jsonObj["roomInfo"]["tLiveInfo"]["lYyid"].ToInt64(),
                    topSid,
                    subSid
                ),
                Url = "https://www.huya.com/" + roomId
            };
        }

        public async Task<LiveAudienceMetrics> GetDeferredAudienceMetrics(LiveRoomDetail roomDetail)
        {
            var urlData = roomDetail?.Data as HuyaUrlDataModel;
            if (roomDetail == null || !roomDetail.Status || urlData == null)
            {
                return new LiveAudienceMetrics()
                {
                    AllowPopularityFallback = true,
                };
            }

            CoreDebug.Log(() => $"[Huya] 首次Playing后开始请求真实人数 roomId={roomDetail.RoomID} pid={urlData.PresenterUid}");
            var viewerCount = await GetViewerCount(urlData.PresenterUid, urlData.Uid);
            if (viewerCount.HasValue)
            {
                return new LiveAudienceMetrics()
                {
                    ViewerCount = viewerCount,
                    ViewerCountSource = "wupui.getUserOnlineRank.iTotal",
                    AllowPopularityFallback = false,
                };
            }

            var vipCount = await GetVipCount(urlData.PresenterUid, urlData.Uid);
            return new LiveAudienceMetrics()
            {
                VipCount = vipCount,
                VipCountSource = vipCount.HasValue ? "liveui.getVipBarListStat.iTotal/iTotalNum" : null,
                AllowPopularityFallback = !vipCount.HasValue,
            };
        }

        private async Task<long?> GetViewerCount(long presenterUid, string uid)
        {
            if (presenterUid <= 0)
            {
                CoreDebug.Log(() => "[Huya] UserOnlineRank skipped: presenter uid empty");
                return null;
            }

            var req = new GetUserOnlineRankReq()
            {
                lPid = presenterUid,
                tId = new HuyaUserId()
                {
                    lUid = uid.ToInt64(),
                    sHuYaUA = "pc_exe&7060000&official"
                }
            };

            var resp = await wupuiTupHttpHelper.GetAsync(req, "getUserOnlineRank", new GetUserOnlineRankRsp());
            CoreDebug.Log(() => $"[Huya] UserOnlineRank pid={presenterUid} iTotal={resp?.iTotal} msg={resp?.sMsg}");
            if (resp != null && resp.iTotal > 0)
            {
                return resp.iTotal;
            }

            return null;
        }

        private async Task<long?> GetVipCount(long presenterUid, string uid)
        {
            if (presenterUid <= 0)
            {
                CoreDebug.Log(() => "[Huya] VipBarListStat skipped: presenter uid empty");
                return null;
            }

            var userUid = uid.ToInt64();
            var webResult = await GetVipCount(presenterUid, userUid, "webh5&0&websocket");
            if (webResult.HasValue)
            {
                return webResult;
            }

            return await GetVipCount(presenterUid, userUid, "pc_exe&7060000&official");
        }

        private async Task<long?> GetVipCount(long presenterUid, long userUid, string huyaUa)
        {
            var req = new VipListStatReq()
            {
                lPid = presenterUid,
                tUserId = new HuyaUserId()
                {
                    lUid = userUid,
                    sHuYaUA = huyaUa
                }
            };

            var resp = await tupHttpHelper.GetAsync(req, "getVipBarListStat", new VipBarListStatInfo());
            var total = 0;
            if (resp != null)
            {
                total = resp.iTotal > 0 ? resp.iTotal : resp.iTotalNum;
            }
            CoreDebug.Log(() => $"[Huya] VipBarListStat pid={presenterUid} ua={huyaUa} respPid={resp?.lPid} iTotal={resp?.iTotal} iTotalNum={resp?.iTotalNum}");
            if (resp != null && resp.lPid == presenterUid)
            {
                return Math.Max(0, total);
            }
            if (total > 0)
            {
                return total;
            }
            return null;
        }

        private static long TryGetPresenterUid(JToken roomInfo)
        {
            if (roomInfo == null)
            {
                return 0;
            }

            var candidates = new[]
            {
                roomInfo["tLiveInfo"]?["lUid"],
                roomInfo["tLiveInfo"]?["lChannel"],
                roomInfo["tLiveInfo"]?["lLiveChannel"],
                roomInfo["tLiveInfo"]?["lChannelId"],
                roomInfo["tProfileInfo"]?["lUid"],
                roomInfo["tProfileInfo"]?["lPid"]
            };

            foreach (var candidate in candidates)
            {
                var value = candidate.ToInt64();
                if (value > 0)
                {
                    return value;
                }
            }

            return 0;
        }

        private async Task<JToken> GetRoomInfo(object roomId)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("user-agent", "Mozilla/5.0 (Linux; Android 11; Pixel 5) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.91 Mobile Safari/537.36 Edg/117.0.0.0");
            var result = await HttpUtil.GetString($"https://m.huya.com/{roomId}", headers);
            var jsonStr = Regex.Match(result, @"window\.HNF_GLOBAL_INIT.=.\{[\s\S]*?\}[\s\S]*?</script>", RegexOptions.Singleline).Groups[0].Value;
            jsonStr = Regex.Replace(jsonStr, @"window\.HNF_GLOBAL_INIT.=.", "").Replace("</script>", "");
            jsonStr = Regex.Replace(jsonStr, @"function.*?\(.*?\).\{[\s\S]*?\}", "\"\"");


            var jsonObj = JObject.Parse(jsonStr);

            var topSid = result.MatchText(@"lChannelId"":([0-9]+)").ToInt64();
            var subSid = result.MatchText(@"lSubChannelId"":([0-9]+)").ToInt64();

            jsonObj["topSid"] = topSid;
            jsonObj["subSid"] = subSid;

            return jsonObj;
        }
        private long GetUuid()
        {
            return (long)((DateTimeOffset.Now.ToUnixTimeMilliseconds() % 10000000000 * 1000 + (1000 * new Random().Next(0, int.MaxValue))) % uint.MaxValue);
        }
        private async Task<string> GetUid()
        {
            var data = "{\"appId\":5002,\"byPass\":3,\"context\":\"\",\"version\":\"2.4\",\"data\":{}}";
            var result = await HttpUtil.PostJsonString($"https://udblgn.huya.com/web/anonymousLogin", data);
            var obj = JObject.Parse(result);

            return obj["data"]["uid"].ToString();
        }

        public async Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            LiveSearchResult searchResult = new LiveSearchResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var headers = new Dictionary<string, string>()
            {
                { "user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"},
                { "referer", "https://www.huya.com/"}
            };

            var result = await HttpUtil.GetUtf8String($"https://search.cdn.huya.com/?m=Search&do=getSearchContent&q={Uri.EscapeDataString(keyword)}&uid=0&v=4&typ=-5&livestate=0&rows=20&start={(page - 1) * 20}", headers);
            var obj = JObject.Parse(result);

            foreach (var item in obj["response"]["3"]["docs"])
            {
                var cover = item["game_screenshot"].ToString();
                if (!cover.Contains("?"))
                {
                    cover += "?x-oss-process=style/w338_h190&";
                }
                searchResult.Rooms.Add(new LiveRoomItem()
                {
                    Cover = cover,
                    Online = item["game_total_count"].ToInt32(),
                    RoomID = item["room_id"].ToString(),
                    Title = item["game_roomName"].ToString(),
                    UserName = item["game_nick"].ToString(),
                });
            }
            searchResult.HasMore = obj["response"]["3"]["numFound"].ToInt32() > (page * 20);
            return searchResult;
        }
        public Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            List<LivePlayQuality> qualities = new List<LivePlayQuality>();
            var urlData = roomDetail.Data as HuyaUrlDataModel;
            if (urlData.BitRates.Count == 0)
            {
                urlData.BitRates = new List<HuyaBitRateModel>() {
                    new HuyaBitRateModel()
                    {
                        Name="原画",
                        BitRate=0,
                    },
                    new HuyaBitRateModel()
                    {
                        Name="高清",
                        BitRate=2000
                    },
                };
            }
            //if (urlData.Lines.Count == 0)
            //{
            //    urlData.Lines = new List<HuyaLineModel>() {
            //        new HuyaLineModel()
            //        {
            //            Line="tx.flv.huya.com",
            //            LineType= HuyaLineType.FLV
            //        },
            //        new HuyaLineModel()
            //        {
            //            Line="bd.flv.huya.com",
            //            LineType= HuyaLineType.FLV
            //        },
            //        new HuyaLineModel()
            //        {
            //            Line="al.flv.huya.com",
            //            LineType= HuyaLineType.FLV
            //        },
            //        new HuyaLineModel()
            //        {
            //            Line="hw.flv.huya.com",
            //            LineType= HuyaLineType.FLV
            //        },
            //    };
            //}
            //var url = GetRealUrl(urlData.Url);

            foreach (var item in urlData.BitRates)
            {
                //var urls = new List<string>();
                //foreach (var line in urlData.Lines)
                //{
                //    var src = line.Line;

                //    src += $"/{line.StreamName}";
                //    if (line.LineType == HuyaLineType.FLV)
                //    {
                //        src += ".flv";
                //    }
                //    if (line.LineType == HuyaLineType.HLS)
                //    {
                //        src += ".m3u8";
                //    }

                //    var param = ProcessAnticode(line.LineType == HuyaLineType.FLV ? line.FlvAntiCode : line.HlsAntiCode, urlData.Uid, line.StreamName);

                //    src += $"?{param}";

                //    if (item.BitRate > 0)
                //    {
                //        src = $"{src}&ratio={item.BitRate}";
                //    }
                //    urls.Add(src);
                //}

                qualities.Add(new LivePlayQuality()
                {
                    Data = new HuyaQualityData()
                    {
                        BitRate = item.BitRate,
                        Lines = urlData.Lines,
                    },
                    Quality = item.Name,
                });
            }




            return Task.FromResult(qualities);
        }
        public string ProcessAnticode(string anticode, string uid, string streamname)
        {
            // https://github.com/iceking2nd/real-url/blob/master/huya.py
            var query = HttpUtility.ParseQueryString(anticode);
            query["t"] = "103";
            query["ctype"] = "tars_mobile";
            var wsTime = (Utils.GetTimestamp() + 21600).ToString("x");
            var seqId = (Utils.GetTimestampMs() + long.Parse(uid)).ToString();
            var fm = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(query["fm"])));
            var wsSecretPrefix = fm.Split('_').First();
            var wsSecretHash = Utils.ToMD5($"{seqId}|{query["ctype"]}|{query["t"]}");
            var wsSecret = Utils.ToMD5($"{wsSecretPrefix}_{uid}_{streamname}_{wsSecretHash}_{wsTime}");


            var map = new NameValueCollection();
            map.Add("wsSecret", wsSecret);
            map.Add("wsTime", wsTime);
            map.Add("seqid", seqId);
            map.Add("ctype", query["ctype"]);
            map.Add("ver", "1");
            map.Add("fs", query["fs"]);
            //map.Add("sphdcdn", query["sphdcdn"] ?? "");
            //map.Add("sphdDC", query["sphdDC"] ?? "");
            //map.Add("sphd", query["sphd"] ?? "");
            //map.Add("exsphd", query["exsphd"] ?? "");
            map.Add("uid", uid);
            map.Add("uuid", GetUuid().ToString());
            map.Add("t", query["t"]);
            map.Add("sv", "202411221719");

            map.Add("dMod", "mseh-0");
            map.Add("sdkPcdn", "1_1");
            map.Add("sdk_sid", "1732862566708");
            map.Add("a_block", "0");


            //将map转为字符串
            var param = string.Join("&", map.AllKeys.Select(x => $"{x}={Uri.EscapeDataString(map[x])}"));
            return param;
        }

        private string BuildAntiCodeEx(string stream, long presenterUid, string antiCode)
        {
            if (string.IsNullOrEmpty(antiCode))
            {
                return antiCode;
            }
            var mapAnti = HttpUtility.ParseQueryString(antiCode);
            var fmValue = mapAnti["fm"];
            var wsTime = mapAnti["wsTime"];
            if (string.IsNullOrEmpty(fmValue) || string.IsNullOrEmpty(wsTime))
            {
                return antiCode;
            }

            var ctype = mapAnti["ctype"] ?? "huya_pc_exe";
            var platformId = 0;
            int.TryParse(mapAnti["t"], out platformId);
            var isWap = platformId == 103;
            var calcStartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var seqId = presenterUid + calcStartTime;

            var secretHash = Utils.ToMD5($"{seqId}|{ctype}|{platformId}");
            var convertUid = Rotl64(presenterUid);
            var calcUid = isWap ? presenterUid : convertUid;
            var fm = Uri.UnescapeDataString(fmValue);
            string secretPrefix;
            try
            {
                secretPrefix = Encoding.UTF8.GetString(Convert.FromBase64String(fm)).Split('_').First();
            }
            catch
            {
                return antiCode;
            }
            var secretStr = $"{secretPrefix}_{calcUid}_{stream}_{secretHash}_{wsTime}";
            var wsSecret = Utils.ToMD5(secretStr);

            var rnd = new Random();
            long wsTimeHex = 0;
            long.TryParse(wsTime, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out wsTimeHex);
            var ct = (long)((wsTimeHex + rnd.NextDouble()) * 1000);
            var uuid = (long)(((ct % 1e10) + rnd.NextDouble()) * 1e3 % 0xffffffff);

            var map = new NameValueCollection
            {
                { "wsSecret", wsSecret },
                { "wsTime", wsTime },
                { "seqid", seqId.ToString() },
                { "ctype", ctype },
                { "ver", "1" },
                { "fs", mapAnti["fs"] ?? "" },
                { "fm", fm },
                { "t", platformId.ToString() }
            };

            if (isWap)
            {
                map.Add("uid", presenterUid.ToString());
                map.Add("uuid", uuid.ToString());
            }
            else
            {
                map.Add("u", convertUid.ToString());
            }

            var param = string.Join("&", map.AllKeys.Select(x => $"{x}={Uri.EscapeDataString(map[x])}"));
            return param;
        }

        private static long Rotl64(long value)
        {
            var low = value & 0xFFFFFFFFL;
            var rotatedLow = ((low << 8) | (low >> 24)) & 0xFFFFFFFFL;
            var high = value & ~0xFFFFFFFFL;
            return high | rotatedLow;
        }

        public async Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn)
        {
            var data = qn.Data as HuyaQualityData;
            var urls = new List<string>();
            foreach (var line in data.Lines)
            {
                var wupUrl = await GetRealUrl(line, data.BitRate);
                if (!string.IsNullOrEmpty(wupUrl))
                {
                    urls.Add(wupUrl);
                    CoreDebug.Log(() => $"[Huya] Url[wup] {wupUrl}");
                }
                var legacyUrl = BuildLegacyUrl(line, data.BitRate, (roomDetail.Data as HuyaUrlDataModel)?.Uid);
                if (!string.IsNullOrEmpty(legacyUrl))
                {
                    urls.Add(legacyUrl);
                    CoreDebug.Log(() => $"[Huya] Url[legacy] {legacyUrl}");
                }
            }

            return urls;
        }

        private string BuildLegacyUrl(HuyaLineModel line, int bitrate, string uid)
        {
            if (string.IsNullOrEmpty(line?.FlvAntiCode) || string.IsNullOrEmpty(uid))
            {
                return null;
            }
            var src = line.Line;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(line.StreamName))
            {
                return null;
            }
            var param = ProcessAnticode(line.FlvAntiCode, uid, line.StreamName);
            var url = $"{src}/{line.StreamName}.flv?{param}";
            if (bitrate > 0)
            {
                url += $"&ratio={bitrate}";
            }
            return url;
        }

        private async Task<string> GetRealUrl(HuyaLineModel line, int bitrate)
        {
            CoreDebug.Log(() => $"[Huya] GetRealUrl line={line.Line} stream={line.StreamName} cdnType={line.CdnType} bitrate={bitrate}");
            var token = await GetCdnTokenInfoEx(line);
            if (string.IsNullOrEmpty(token))
            {
                CoreDebug.Log(() => "[Huya] TokenEx为空，回退到旧token生成");
                return "";
            }
            var antiCode = BuildAntiCodeEx(line.StreamName, line.PresenterUid, token);
            var suffix = line.LineType == HuyaLineType.HLS ? "m3u8" : "flv";
            var url = $"{line.Line}/{line.StreamName}.{suffix}?{antiCode}&codec=264";
            if (bitrate > 0)
            {
                url += $"&ratio={bitrate}";
            }
            return url;
        }

        private async Task<string> GetCdnTokenInfoEx(HuyaLineModel line)
        {
            var req = new HYGetCdnTokenExReq();
            req.sStreamName = line.StreamName;
            req.sFlvUrl = line.Line;
            req.iLoopTime = 0;
            req.iAppId = 66;
            req.tId = new HuyaUserId()
            {
                sHuYaUA = "pc_exe&7060000&official"
            };
            var resp = await tupHttpHelper.GetAsync(req, "getCdnTokenInfoEx", new HYGetCdnTokenExResp());
            var token = resp?.sFlvToken ?? "";
            CoreDebug.Log(() => $"[Huya] TokenExResp len={token.Length} exp={resp?.iExpireTime}");
            if (!string.IsNullOrEmpty(token))
            {
                var query = HttpUtility.ParseQueryString(token);
                var wsTime = query["wsTime"];
                if (!string.IsNullOrEmpty(wsTime) && long.TryParse(wsTime, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds))
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    var remain = (dt - DateTimeOffset.UtcNow).TotalSeconds;
                    CoreDebug.Log(() => $"[Huya] TokenEx wsTime={wsTime} exp={dt:O} remain={remain:F0}s");
                }
                var wsSecret = query["wsSecret"];
                if (!string.IsNullOrEmpty(wsSecret))
                {
                    CoreDebug.Log(() => $"[Huya] TokenEx wsSecretLen={wsSecret.Length}");
                }
                CoreDebug.Log(() => $"[Huya] TokenEx ctype={query["ctype"]} t={query["t"]} fs={query["fs"]}");
            }
            return token;
        }

        public async Task<bool> GetLiveStatus(object roomId)
        {
            var roomInfo = await GetRoomInfo(roomId.ToString());
            return roomInfo["roomInfo"]["eLiveStatus"].ToInt32() == 2;
        }
        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId)
        {
            return Task.FromResult(new List<LiveSuperChatMessage>());
        }
    }
    public class HuyaUrlDataModel
    {
        public string Url { get; set; }
        public string Uid { get; set; }
        public long UUid { get; set; }
        public long PresenterUid { get; set; }
        public List<HuyaLineModel> Lines { get; set; }
        public List<HuyaBitRateModel> BitRates { get; set; }
    }
    public enum HuyaLineType
    {
        FLV = 0,
        HLS = 1,
    }
    public class HuyaLineModel
    {
        public string Line { get; set; }
        public string FlvAntiCode { get; set; }
        public string StreamName { get; set; }
        public string HlsAntiCode { get; set; }
        public string CdnType { get; set; }
        public long PresenterUid { get; set; }
        public HuyaLineType LineType { get; set; }
    }
    public class HuyaBitRateModel
    {
        public string Name { get; set; }
        public int BitRate { get; set; }

    }
    public class HuyaQualityData
    {
        public int BitRate { get; set; }
        public List<HuyaLineModel> Lines { get; set; }
    }
}
