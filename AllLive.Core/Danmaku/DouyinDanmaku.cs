using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;
using AllLive.Core.Danmaku.Proto;
using ProtoBuf;
using System.IO;
using System.Security.Cryptography;
using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AllLive.Core.Danmaku
{
    public class DouyinDanmakuArgs
    {
        public string WebRid { get; set; }
        public string RoomId { get; set; }
        public string UserId { get; set; }
        public string Cookie { get; set; }
    }
    public class DouyinDanmaku : ILiveDanmaku
    {
        public int HeartbeatTime => 10 * 1000;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        private string baseUrl = "wss://webcast3-ws-web-lq.douyin.com/webcast/im/push/v2/";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0";
        private const string VersionCode = "180800";
        private const string WebcastSdkVersion = "1.0.14-beta.0";
        private const string UpdateVersionCode = WebcastSdkVersion;
        private static readonly Lazy<string> WebMssdkScript = new Lazy<string>(LoadWebMssdkScript);

        Timer timer;
        WebSocket ws;
        DouyinDanmakuArgs danmakuArgs;
        bool isStopped = true;
        private string ServerUrl { get; set; }
        private string BackupUrl { get; set; }

        public async Task Start(object args)
        {
            isStopped = false;
            danmakuArgs = args as DouyinDanmakuArgs;
            var ts = Utils.GetTimestampMs();
            var query = new Dictionary<string, string>()
            {
            { "app_name", "douyin_web" },
            { "version_code", VersionCode },
            { "webcast_sdk_version", WebcastSdkVersion },
            { "update_version_code", UpdateVersionCode },
            { "compress", "gzip" },
            // {"internal_ext", $"internal_src:dim|wss_push_room_id:{danmakuArgs.roomId}|wss_push_did:{danmakuArgs.userId}|dim_log_id:20230626152702E8F63662383A350588E1|fetch_time:1687764422114|seq:1|wss_info:0-1687764422114-0-0|wrds_kvs:WebcastRoomRankMessage-1687764036509597990_InputPanelComponentSyncData-1687736682345173033_WebcastRoomStatsMessage-1687764414427812578"},
            { "cursor", $"h-1_t-{ts}_r-1_d-1_u-1" },
            { "host", "https://live.douyin.com" },
            { "aid", "6383" },
            { "live_id", "1" },
            { "did_rule", "3" },
            { "debug", "false" },
            { "maxCacheMessageNumber", "20" },
            { "endpoint", "live_pc" },
            { "support_wrds", "1" },
            { "im_path", "/webcast/im/fetch/" },
            { "user_unique_id", danmakuArgs.UserId },
            { "device_platform", "web" },
            { "cookie_enabled", "true" },
            { "screen_width", "1920" },
            { "screen_height", "1080" },
            { "browser_language", "zh-CN" },
            { "browser_platform", "Win32" },
            { "browser_name", "Mozilla" },
            { "browser_version", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0" },
            { "browser_online", "true" },
            { "tz_name", "Asia/Shanghai" },
            { "identity", "audience" },
            { "room_id", danmakuArgs.RoomId },
            { "heartbeatDuration", "0" },
            //{ "signature", "00000000" }
            };

            var sign = await GetSign(danmakuArgs.RoomId, danmakuArgs.UserId);
            if (isStopped)
            {
                return;
            }
            query.Add("signature", sign ?? "");
            if (string.IsNullOrWhiteSpace(sign))
            {
                CoreDebug.Log(() => "[DouyinDanmaku] signature为空，将尝试无签名连接");
            }

            // 将参数拼接到url
            var url = $"{baseUrl}?{Utils.BuildQueryString(query)}";
            ServerUrl = url;
            BackupUrl = url.Replace("webcast3-ws-web-lq", "webcast5-ws-web-lf");
            ws = new WebSocket(ServerUrl);
            // 添加请求头
            ws.CustomHeaders = new Dictionary<string, string>() {
                {"Origin","https://live.douyin.com" },
                {"Cookie", danmakuArgs.Cookie ?? "" },
                {"User-Agent",UserAgent }
              };
            CoreDebug.Log(() => $"[DouyinDanmaku] 连接WS roomId={danmakuArgs.RoomId} userId={danmakuArgs.UserId} signLen={sign?.Length ?? 0} cookieLen={danmakuArgs.Cookie?.Length ?? 0} urlLen={url.Length}");
            // 必须设置ssl协议为Tls12
            ws.SslConfiguration.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;

            ws.OnOpen += Ws_OnOpen;
            ws.OnError += Ws_OnError;
            ws.OnMessage += Ws_OnMessage;
            ws.OnClose += Ws_OnClose;
            timer = new Timer(HeartbeatTime);
            timer.Elapsed += Timer_Elapsed;
            await Task.Run(() =>
            {
                if (!isStopped)
                {
                    ws?.Connect();
                }
            });
        }
        private async void Ws_OnOpen(object sender, EventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            CoreDebug.Log(() => "[DouyinDanmaku] WebSocket已连接");
            await Task.Run(() =>
            {
                if (isStopped)
                {
                    return;
                }
                //发送进房信息
                SendHeartBeatData();

            });
            if (!isStopped)
            {
                timer?.Start();
            }

        }

        private async void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            try
            {
                var message = e.RawData;
                var wssPackage = DeserializeProto<PushFrame>(message);
                var logId = wssPackage.logId;
                var decompressed = GzipDecompress(wssPackage.Payload);
                var payloadPackage = DeserializeProto<Response>(decompressed);
                if (payloadPackage.needAck ?? false)
                {
                    await Task.Run(() =>
                    {
                        SendACKData(logId ?? 0, payloadPackage.internalExt);
                    });

                }

                foreach (var msg in payloadPackage.messagesLists)
                {
                    if (msg.Method == "WebcastChatMessage")
                    {
                        UnPackWebcastChatMessage(msg.Payload);
                    }
                    else if (msg.Method == "WebcastRoomUserSeqMessage")
                    {
                        UnPackWebcastRoomUserSeqMessage(msg.Payload);
                    }
                }
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] 消息解析异常: {ex.GetType().FullName}: {ex.Message}");
            }
        }
        private void UnPackWebcastChatMessage(byte[] payload)
        {
            try
            {
                var chatMessage = DeserializeProto<ChatMessage>(payload);
                NewMessage?.Invoke(this, new LiveMessage()
                {
                    Type = LiveMessageType.Chat,
                    Color = DanmakuColor.White,
                    Message = chatMessage.Content,
                    UserName = chatMessage.User.nickName,
                });
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] 弹幕解包失败: {ex.GetType().FullName}: {ex.Message}");
            }
        }

        void UnPackWebcastRoomUserSeqMessage(byte[] payload)
        {
            try
            {
                var roomUserSeqMessage = DeserializeProto<RoomUserSeqMessage>(payload);
                var onlineValue = roomUserSeqMessage.onlineUserForAnchor.ParseCountTextToLong()
                    ?? roomUserSeqMessage.totalUserStr.ParseCountTextToLong()
                    ?? roomUserSeqMessage.totalUser;
                if (!onlineValue.HasValue && roomUserSeqMessage.Popularity.HasValue)
                {
                    onlineValue = roomUserSeqMessage.Popularity.Value;
                }
                if (!onlineValue.HasValue)
                {
                    return;
                }

                NewMessage?.Invoke(this, new LiveMessage()
                {
                    Type = LiveMessageType.Online,
                    Data = onlineValue.Value,
                    Color = DanmakuColor.White,
                    Message = "",
                    UserName = "",
                });
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] 在线人数解包失败: {ex.GetType().FullName}: {ex.Message}");
            }

        }
        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            CoreDebug.Log(() => $"[DouyinDanmaku] WebSocket关闭 code={e.Code} clean={e.WasClean} reason={e.Reason}");
            OnClose?.Invoke(this, e.Reason);
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            CoreDebug.Log(() => $"[DouyinDanmaku] WebSocket错误: {e.Message}{(e.Exception == null ? "" : $" | {e.Exception.GetType().FullName}: {e.Exception.Message}")}");
            OnClose?.Invoke(this, e.Message);
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            Heartbeat();
        }

        public async void Heartbeat()
        {
            if (isStopped)
            {
                return;
            }

            await Task.Run(() =>
            {
                if (isStopped)
                {
                    return;
                }
                SendHeartBeatData();
            });
        }


        public async Task Stop()
        {
            isStopped = true;
            StopTimer();
            await Task.Run(() =>
            {
                var socket = ws;
                ws = null;
                if (socket == null)
                {
                    return;
                }
                DetachWebSocketEvents(socket);
                try { socket.Close(); } catch { }
            });
        }

        private void StopTimer()
        {
            var currentTimer = timer;
            timer = null;
            if (currentTimer == null)
            {
                return;
            }
            currentTimer.Stop();
            currentTimer.Elapsed -= Timer_Elapsed;
            currentTimer.Dispose();
        }

        private void DetachWebSocketEvents(WebSocket socket)
        {
            if (socket == null)
            {
                return;
            }
            socket.OnOpen -= Ws_OnOpen;
            socket.OnError -= Ws_OnError;
            socket.OnMessage -= Ws_OnMessage;
            socket.OnClose -= Ws_OnClose;
        }

        private void SendHeartBeatData()
        {
            var socket = ws;
            if (isStopped || socket == null)
            {
                return;
            }

            var obj = new PushFrame();
            obj.payloadType = "hb";

            socket.Send(SerializeProto(obj));

        }
        private void SendACKData(ulong logId, string internalExt)
        {
            var socket = ws;
            if (isStopped || socket == null)
            {
                return;
            }

            var obj = new PushFrame();

            obj.payloadType = "ack";
            obj.logId = logId;
            var payloadText = internalExt ?? string.Empty;
            obj.Payload = Encoding.UTF8.GetBytes(payloadText);

            socket.Send(SerializeProto(obj));

        }
        public static byte[] GzipDecompress(byte[] bytes)
        {
            using (var memoryStream = new MemoryStream(bytes))
            {

                using (var outputStream = new MemoryStream())
                {
                    using (var decompressStream = new GZipStream(memoryStream, CompressionMode.Decompress))
                    {
                        decompressStream.CopyTo(outputStream);
                    }
                    return outputStream.ToArray();
                }
            }
        }

        private static byte[] SerializeProto(object obj)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, obj);
                    var buffer = ms.GetBuffer();
                    var dataBuffer = new byte[ms.Length];
                    Array.Copy(buffer, dataBuffer, ms.Length);
                    ms.Dispose();
                    return dataBuffer;
                }
            }
            catch
            {
                return null;
            }
        }
        private static T DeserializeProto<T>(byte[] bufferData)
        {

            using (MemoryStream ms = new MemoryStream(bufferData))
            {
                return Serializer.Deserialize<T>(ms);
            }

        }

        /// <summary>
        /// 获取Websocket签名
        /// </summary>
        private async Task<string> GetSign(string roomId, string uniqueId)
        {
            var signParam = BuildSignParam(roomId, uniqueId);
            if (string.IsNullOrWhiteSpace(signParam))
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] signature参数为空 roomId={roomId}");
                return "";
            }
            var md5 = Utils.ToMD5(signParam);
            var quickJsResult = TryGetSignByQuickJs(md5, out var quickJsError);
            if (!string.IsNullOrWhiteSpace(quickJsResult))
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] QuickJS签名成功 len={quickJsResult.Length}");
                return quickJsResult;
            }
            if (!string.IsNullOrWhiteSpace(quickJsError))
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] QuickJS签名失败: {quickJsError}");
            }
            var serviceResult = await GetSignByService(roomId, uniqueId);
            if (!string.IsNullOrWhiteSpace(serviceResult))
            {
                return serviceResult;
            }
            CoreDebug.Log(() => $"[DouyinDanmaku] signature获取失败 roomId={roomId}");
            return "";
        }

        private static string BuildSignParam(string roomId, string uniqueId)
        {
            var pairs = new List<KeyValuePair<string, string>>()
            {
                new KeyValuePair<string, string>("live_id", "1"),
                new KeyValuePair<string, string>("aid", "6383"),
                new KeyValuePair<string, string>("version_code", VersionCode),
                new KeyValuePair<string, string>("webcast_sdk_version", WebcastSdkVersion),
                new KeyValuePair<string, string>("room_id", roomId ?? ""),
                new KeyValuePair<string, string>("sub_room_id", ""),
                new KeyValuePair<string, string>("sub_channel_id", ""),
                new KeyValuePair<string, string>("did_rule", "3"),
                new KeyValuePair<string, string>("user_unique_id", uniqueId ?? ""),
                new KeyValuePair<string, string>("device_platform", "web"),
                new KeyValuePair<string, string>("device_type", ""),
                new KeyValuePair<string, string>("ac", ""),
                new KeyValuePair<string, string>("identity", "audience"),
            };
            return string.Join(",", pairs.Select(item => $"{item.Key}={item.Value}"));
        }

        private static string TryGetSignByQuickJs(string md5Param, out string error)
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
                    ? contextType.GetMethod("Eval", new[] { typeof(string), typeof(string) })
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

                var script = WebMssdkScript.Value;
                if (string.IsNullOrWhiteSpace(script))
                {
                    error = "webmssdk脚本为空";
                    return null;
                }
                Eval(script);
                var result = Eval($"get_sign('{EscapeJs(md5Param)}')");
                if (string.IsNullOrWhiteSpace(result))
                {
                    error = "get_sign返回空";
                    return null;
                }
                return result;
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

        private async Task<string> GetSignByService(string roomId, string uniqueId)
        {
            var payload = JsonConvert.SerializeObject(new { roomId, uniqueId });
            foreach (var url in GetSignServiceUrls())
            {
                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = url.Contains("127.0.0.1") || url.Contains("localhost")
                            ? TimeSpan.FromSeconds(5)
                            : TimeSpan.FromSeconds(10);
                        var start = DateTimeOffset.UtcNow;
                        using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                        using (var response = await client.PostAsync(url, content))
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            var elapsedMs = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                            CoreDebug.Log(() => $"[DouyinDanmaku] signature服务 url={url} status={(int)response.StatusCode} {response.ReasonPhrase} elapsedMs={elapsedMs:F0} bodyLen={body?.Length ?? 0}");
                            if (!response.IsSuccessStatusCode)
                            {
                                CoreDebug.Log(() => $"[DouyinDanmaku] signature服务非200 url={url} bodyHead={TrimForLog(body)}");
                                continue;
                            }
                            var json = JObject.Parse(body);
                            var data = json["data"];
                            var sign = data?["signature"]?.ToString();
                            if (string.IsNullOrWhiteSpace(sign))
                            {
                                sign = data?.ToString();
                            }
                            if (string.IsNullOrWhiteSpace(sign))
                            {
                                sign = json["signature"]?.ToString();
                            }
                            if (!string.IsNullOrWhiteSpace(sign))
                            {
                                CoreDebug.Log(() => $"[DouyinDanmaku] signature服务成功 url={url} signLen={sign.Length}");
                                return sign;
                            }
                            CoreDebug.Log(() => $"[DouyinDanmaku] signature服务返回空 url={url} code={json["code"]} msg={json["msg"]}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoreDebug.Log(() => $"[DouyinDanmaku] signature服务异常 url={url} err={ex.GetType().FullName} 0x{ex.HResult:X8} {ex.Message}");
                }
            }
            return "";
        }

        private static IEnumerable<string> GetSignServiceUrls()
        {
            try
            {
                var configured = CoreConfig.GetDouyinSignServiceUrls();
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
                    var env = Environment.GetEnvironmentVariable("ALLLIVE_DOUYIN_SIGN_URL");
                    if (!string.IsNullOrWhiteSpace(env))
                    {
                        urls.AddRange(SplitUrls(env));
                    }
                }
                catch
                {
                }
            }
            urls.Add("http://127.0.0.1:8788/api/douyin/sign");
            urls.Add("http://localhost:8788/api/douyin/sign");
            urls.Add("https://dy.nsapps.cn/signature");
            return urls.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
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

        private static string LoadWebMssdkScript()
        {
            try
            {
                var assembly = typeof(DouyinDanmaku).Assembly;
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("douyin-webmssdk.js", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(resourceName))
                {
                    CoreDebug.Log(() => "[DouyinDanmaku] webmssdk脚本资源未找到");
                    return "";
                }
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        CoreDebug.Log(() => "[DouyinDanmaku] webmssdk脚本资源读取失败");
                        return "";
                    }
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                CoreDebug.Log(() => $"[DouyinDanmaku] webmssdk脚本加载失败: {ex.GetType().FullName}: {ex.Message}");
                return "";
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

        private static string EscapeJs(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }
}
