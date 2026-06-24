using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AllLive.Core.Danmaku;
using AllLive.Core.Helper;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Linq;
using System.Net.Http;
using System.Text;


/*
 * 参考：
 * https://github.com/wbt5/real-url/blob/master/douyu.py
 */
namespace AllLive.Core
{
    public class Douyu : ILiveSite
    {
        public string Name => "斗鱼直播";
        public ILiveDanmaku GetDanmaku() => new DouyuDanmaku();
        public async Task<List<LiveCategory>> GetCategores()
        {
            List<LiveCategory> categories = new List<LiveCategory>();
            var result = await HttpUtil.GetString("https://m.douyu.com/api/cate/list");
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

      
        public async Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/gapi/rkc/directory/mixList/2_{ category.ID}/{page}");
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
        public async Task<LiveCategoryResult> GetRecommendRooms(int page = 1)
        {
            LiveCategoryResult categoryResult = new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/japi/weblist/apinc/allpage/6/{page}");
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
        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            var roomInfo = await GetRoomInfo(roomId.ToString());
            var jsEncResult = await HttpUtil.GetString($"https://www.douyu.com/swf_api/homeH5Enc?rids={roomId}", new Dictionary<string, string>()
            {
                { "referer", $"https://m.douyu.com/{roomId}"},
                { "user-agent","Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1 Edg/114.0.0.0" },
            });
            var crptext = JObject.Parse(jsEncResult)["data"][$"room{roomId}"].ToString();
           

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
                Status = roomInfo["show_status"].ToInt32() == 1 && roomInfo["videoLoop"].ToInt32() != 1,
                DanmakuData = roomInfo["room_id"].ToString(),
                Data = await GetPlayArgs(crptext, roomInfo["room_id"].ToString()),
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


        private async Task<JToken> GetRoomInfo(string roomId)
        {
            var result = await HttpUtil.GetString($"https://www.douyu.com/betard/{roomId}", new Dictionary<string, string>()
            {
                { "referer", $"https://www.douyu.com/{roomId}"},
                { "user-agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36 Edg/114.0.1823.43" },
            });
            var obj = JObject.Parse(result);
            return obj["room"];
        }

        private async Task<string> GetPlayArgs(string html, string rid)
        {
            var rawLen = html?.Length ?? 0;
            //取加密的js
            html = Regex.Match(html, @"(vdwdae325w_64we[\s\S]*function ub98484234[\s\S]*?)function").Groups[1].Value;
            html = Regex.Replace(html, @"eval.*?;}", "strc;}");
            CoreDebug.Log(() => $"[Douyu] GetPlayArgs rid={rid} rawLen={rawLen} jsLen={html?.Length ?? 0} uwp={IsUwpRuntime()}");
            if (string.IsNullOrEmpty(html))
            {
                CoreDebug.Log(() => "[Douyu] GetPlayArgs: 签名JS提取为空");
            }

            if (IsUwpRuntime())
            {
                return await GetPlayArgsByService(html, rid, "uwp");
            }

            var quickJsResult = TryGetPlayArgsByQuickJs(html, rid, out var quickJsError);
            if (!string.IsNullOrEmpty(quickJsResult))
            {
                CoreDebug.Log(() => $"[Douyu] QuickJS签名成功 len={quickJsResult.Length}");
                return quickJsResult;
            }
            if (!string.IsNullOrEmpty(quickJsError))
            {
                CoreDebug.Log(() => $"[Douyu] QuickJS签名失败: {quickJsError}");
            }
            return await GetPlayArgsByService(html, rid, "fallback");
        }

        private static string TryGetPlayArgsByQuickJs(string html, string rid, out string error)
        {
            error = null;
            object runtime = null;
            object context = null;
            try
            {
                var runtimeType = Type.GetType("QuickJS.QuickJSRuntime, QuickJS.NET");
                if (runtimeType == null)
                {
                    error = "QuickJSRuntime类型未找到";
                    return null;
                }
                runtime = Activator.CreateInstance(runtimeType);
                var createContext = runtimeType.GetMethod("CreateContext", Type.EmptyTypes);
                context = createContext?.Invoke(runtime, null);
                if (context == null)
                {
                    error = "CreateContext返回空";
                    return null;
                }

                var contextType = context.GetType();
                var flagsType = Type.GetType("QuickJS.JSEvalFlags, QuickJS.NET");
                object flags = null;
                var evalMethod = flagsType == null
                    ? null
                    : contextType.GetMethod("Eval", new[] { typeof(string), typeof(string), flagsType });
                if (evalMethod == null)
                {
                    evalMethod = contextType.GetMethod("Eval", new[] { typeof(string), typeof(string) });
                }
                if (evalMethod == null)
                {
                    error = "Eval方法未找到";
                    return null;
                }
                if (flagsType != null && evalMethod.GetParameters().Length == 3)
                {
                    flags = Enum.Parse(flagsType, "Global");
                }

                string Eval(string code)
                {
                    if (evalMethod.GetParameters().Length == 3)
                    {
                        return evalMethod.Invoke(context, new object[] { code, "", flags })?.ToString();
                    }
                    return evalMethod.Invoke(context, new object[] { code, "" })?.ToString();
                }

                var did = "10000000000000000000000000001501";
                var time = Core.Helper.Utils.GetTimestamp();

                Eval(html);
                var jsCode = Eval("ub98484234()");
                if (string.IsNullOrEmpty(jsCode))
                {
                    error = "ub98484234返回空";
                    return null;
                }

                var v = Regex.Match(jsCode, @"v=(\d+)").Groups[1].Value;
                if (string.IsNullOrEmpty(v))
                {
                    error = "签名JS中未提取到v";
                    return null;
                }
                //对参数进行MD5，替换掉JS的CryptoJS\.MD5
                var rb = Core.Helper.Utils.ToMD5(rid + did + time + v);

                var jsCode2 = Regex.Replace(jsCode, @"return rt;}\);?", "return rt;}");
                //设置方法名为sign
                jsCode2 = Regex.Replace(jsCode2, @"\(function \(", "function sign(");
                //将JS中的MD5方法直接替换成加密完成的rb
                jsCode2 = Regex.Replace(jsCode2, @"CryptoJS\.MD5\(cb\)\.toString\(\)", $@"""{rb}""");
                Eval(jsCode2);
                //返回参数
                var args = Eval($"sign('{rid}','{did}','{time}')");
                if (string.IsNullOrEmpty(args))
                {
                    error = "sign返回空";
                    return null;
                }
                return args;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().FullName}: {ex.Message}";
                return null;
            }
            finally
            {
                if (context is IDisposable contextDisposable)
                {
                    contextDisposable.Dispose();
                }
                if (runtime is IDisposable runtimeDisposable)
                {
                    runtimeDisposable.Dispose();
                }
            }
        }

        private static async Task<string> GetPlayArgsByService(string html, string rid, string reason)
        {
            var jsonObj = new
            {
                html = html,
                rid = rid
            };
            var payload = Newtonsoft.Json.JsonConvert.SerializeObject(jsonObj);
            CoreDebug.Log(() => $"[Douyu] 调用签名服务 reason={reason} payloadLen={payload.Length}");
            foreach (var url in GetSignServiceUrls())
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = url.Contains("127.0.0.1") || url.Contains("localhost")
                            ? TimeSpan.FromSeconds(2)
                            : TimeSpan.FromSeconds(10);
                        var start = DateTimeOffset.UtcNow;
                        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                        using (var response = await client.PostAsync(url, content))
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            var elapsedMs = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                            CoreDebug.Log(() => $"[Douyu] 签名服务 url={url} status={(int)response.StatusCode} {response.ReasonPhrase} elapsedMs={elapsedMs:F0} bodyLen={body?.Length ?? 0}");
                            if (!response.IsSuccessStatusCode)
                            {
                                CoreDebug.Log(() => $"[Douyu] 签名服务非200 url={url} bodyHead={TrimForLog(body)}");
                                continue;
                            }
                            var obj = JObject.Parse(body);
                            var code = obj["code"].ToInt32();
                            var data = obj["data"]?.ToString();
                            var msg = obj["msg"]?.ToString();
                            CoreDebug.Log(() => $"[Douyu] 签名服务 url={url} code={code} msg={msg} dataLen={data?.Length ?? 0}");
                            if (code == 0)
                            {
                                return data;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoreDebug.Log(() => $"[Douyu] 签名服务异常 url={url} rid={rid} err={ex.GetType().FullName} 0x{ex.HResult:X8} {ex.Message}");
                }
            }
            return "";
        }

        private static IEnumerable<string> GetSignServiceUrls()
        {
            try
            {
                var configured = CoreConfig.GetDouyuSignServiceUrls();
                if (configured != null && configured.Count > 0)
                {
                    return configured;
                }
            }
            catch
            {
            }

            var urls = new List<string>();
            if (!IsUwpRuntime())
            {
                try
                {
                    var env = Environment.GetEnvironmentVariable("ALLLIVE_DOUYU_SIGN_URL");
                    if (!string.IsNullOrWhiteSpace(env))
                    {
                        urls.AddRange(SplitUrls(env));
                    }
                }
                catch
                {
                }
            }
            urls.Add("http://127.0.0.1:8788/api/douyu/sign");
            urls.Add("http://alive.nsapps.cn/api/AllLive/DouyuSign");
            return urls.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
        }

        private static IEnumerable<string> SplitUrls(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }
            return raw
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x));
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

        private static bool IsUwpRuntime()
        {
            try
            {
                return Type.GetType("Windows.ApplicationModel.Package, Windows, ContentType=WindowsRuntime") != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            LiveSearchResult searchResult = new LiveSearchResult()
            {
                Rooms = new List<LiveRoomItem>(),

            };
            var result = await HttpUtil.GetString($"https://www.douyu.com/japi/search/api/searchShow?kw={ Uri.EscapeDataString(keyword)}&page={ page}&pageSize=20");
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
        public async Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            var data = roomDetail.Data?.ToString();
            if (string.IsNullOrWhiteSpace(data))
            {
                CoreDebug.Log(() => $"[Douyu] GetPlayQuality中止: 播放参数为空 rid={roomDetail.RoomID}");
                return new List<LivePlayQuality>();
            }
            data += $"&cdn=&rate=0";
            CoreDebug.Log(() => $"[Douyu] GetPlayQuality rid={roomDetail.RoomID} dataLen={data.Length}");
            List<LivePlayQuality> qualities = new List<LivePlayQuality>();
            var result = await HttpUtil.PostString($"https://www.douyu.com/lapi/live/getH5Play/{ roomDetail.RoomID}", data);
            var obj = JObject.Parse(result);
            CoreDebug.Log(() => $"[Douyu] GetPlayQuality resp error={obj["error"]} msg={obj["msg"]} cdnCount={(obj["data"]?["cdnsWithName"] as JArray)?.Count ?? 0} rateCount={(obj["data"]?["multirates"] as JArray)?.Count ?? 0}");
            var cdns = new List<string>();
            foreach (var item in obj["data"]["cdnsWithName"])
            {
                cdns.Add(item["cdn"].ToString());
            }
            // 如果cdn以scdn开头，将其放到最后
            for (int i = 0; i < cdns.Count; i++)
            {
                if (cdns[i].StartsWith("scdn"))
                {
                    cdns.Add(cdns[i]);
                    cdns.RemoveAt(i);
                    break;
                }
            }


            foreach (var item in obj["data"]["multirates"])
            {
                qualities.Add(new LivePlayQuality()
                {
                    Quality = item["name"].ToString(),
                    Data = new KeyValuePair<int, List<string>>(item["rate"].ToInt32(), cdns),
                });
            }
            return qualities;
        }
        public async Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn)
        {
            var args = roomDetail.Data.ToString();
            var data = (KeyValuePair<int, List<string>>)qn.Data;
            List<string> urls = new List<string>();
            foreach (var item in data.Value)
            {
                var url = await GetUrl(roomDetail.RoomID, args, data.Key, item);
                if (url.Length != 0)
                {
                    urls.Add(url);
                }
            }
            return urls;
        }

        private async Task<string> GetUrl(string rid, string args, int rate, string cdn = "")
        {
            try
            {
                args += $"&cdn={cdn}&rate={rate}";
                var result = await HttpUtil.PostString($"https://www.douyu.com/lapi/live/getH5Play/{rid}", args);
                var obj = JObject.Parse(result);
                CoreDebug.Log(() => $"[Douyu] GetUrl rid={rid} cdn={cdn} rate={rate} error={obj["error"]} msg={obj["msg"]}");
                return obj["data"]["rtmp_url"].ToString() + "/" + System.Net.WebUtility.HtmlDecode(obj["data"]["rtmp_live"].ToString());
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[Douyu] GetUrl失败 rid={rid} cdn={cdn} rate={rate} err={ex.GetType().FullName}: {ex.Message}");
                return "";
            }

        }
        public async Task<bool> GetLiveStatus(object roomId)
        {
            var roomInfo = await GetRoomInfo(roomId.ToString());
            return roomInfo["show_status"].ToInt32() == 1 && roomInfo["videoLoop"].ToInt32() != 1;
        }
        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId)
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
