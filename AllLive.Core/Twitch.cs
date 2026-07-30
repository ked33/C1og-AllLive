using AllLive.Core.Danmaku;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AllLive.Core
{
    /// <summary>
    /// Twitch 直播支持。
    ///
    /// 取流流程参考 Streamlink 的 Twitch 插件：先用公开 Client-ID 请求匿名
    /// PlaybackAccessToken，再向 Twitch Usher 请求频道 HLS 主清单，最后把选中
    /// 的媒体播放列表交给现有 FFmpeg 播放器。
    /// </summary>
    public class Twitch : ILiveSite, IDeferredAudienceMetricSite
    {
        private const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
        // Twitch 网页播放器当前使用的 PlaybackAccessToken persisted query hash。
        // 该值由 Streamlink 维护，Twitch 变更时需要同步更新。
        private const string PlaybackAccessTokenHash =
            "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9";
        private const string GraphQlUrl = "https://gql.twitch.tv/gql";
        private const string UsherBaseUrl = "https://usher.ttvnw.net";
        private const string TwitchOrigin = "https://www.twitch.tv";
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        private static readonly string DeviceId = Guid.NewGuid().ToString("N");
        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly TimeSpan PlaybackCacheLifetime = TimeSpan.FromSeconds(90);

        private readonly object playbackCacheLock = new object();
        private TwitchPlaybackSnapshot playbackCache;

        public string Name => "Twitch直播";

        public ILiveDanmaku GetDanmaku()
        {
            return new TwitchDanmaku();
        }

        public Task<List<LiveCategory>> GetCategores()
        {
            return Task.FromResult(new List<LiveCategory>());
        }

        public Task<LiveCategoryResult> GetCategoryRooms(LiveSubCategory category, int page = 1)
        {
            return Task.FromResult(new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),
                HasMore = false,
            });
        }

        public Task<LiveCategoryResult> GetRecommendRooms(int page = 1)
        {
            return Task.FromResult(new LiveCategoryResult()
            {
                Rooms = new List<LiveRoomItem>(),
                HasMore = false,
            });
        }

        public Task<LiveSearchResult> Search(string keyword, int page = 1)
        {
            // 主界面会先把 Twitch URL 交给 SiteParser。这里暂不实现 Twitch
            // 的站内搜索，避免把普通关键词误发给 GQL 接口。
            return Task.FromResult(new LiveSearchResult()
            {
                Rooms = new List<LiveRoomItem>(),
                HasMore = false,
            });
        }

        public async Task<LiveRoomDetail> GetRoomDetail(object roomId)
        {
            var channel = NormalizeChannel(roomId);
            var user = await GetChannelUserAsync(channel);
            if (user == null)
            {
                return new LiveRoomDetail()
                {
                    RoomID = channel,
                    Title = "Twitch频道不存在或不可用",
                    UserName = channel,
                    Url = BuildChannelUrl(channel),
                    Status = false,
                    AllowPopularityFallback = false,
                    DanmakuData = null,
                };
            }

            var stream = user["stream"] as JObject;
            var lastBroadcast = user["lastBroadcast"] as JObject;
            var game = stream?["game"] as JObject;
            var isLive = stream != null;
            var streamTitle = ReadString(stream?["title"]);
            var lastBroadcastTitle = ReadString(lastBroadcast?["title"]);
            var title = isLive
                ? (streamTitle ?? "Twitch直播")
                : (lastBroadcastTitle ?? "Twitch频道未开播");
            var viewerCount = ReadLong(stream?["viewersCount"]);
            var preview = ReadString(stream?["previewImageURL"]);
            var avatar = ReadString(user["profileImageURL"]);
            var banner = ReadString(user["bannerImageURL"]);
            var displayName = ReadString(user["displayName"]);
            var description = ReadString(user["description"]);
            var gameName = ReadString(game?["name"]);

            return new LiveRoomDetail()
            {
                RoomID = channel,
                Title = title,
                Cover = preview ?? banner ?? avatar,
                UserName = displayName ?? channel,
                UserAvatar = avatar,
                Online = ToCompatibleOnline(viewerCount),
                ViewerCount = viewerCount,
                ViewerCountSource = viewerCount.HasValue ? "Twitch GQL stream.viewersCount" : null,
                Popularity = null,
                PopularitySource = null,
                VipCount = null,
                VipCountSource = null,
                AllowPopularityFallback = false,
                Introduction = string.IsNullOrWhiteSpace(gameName)
                    ? description
                    : string.IsNullOrWhiteSpace(description)
                        ? $"游戏：{gameName}"
                        : $"游戏：{gameName}\n{description}",
                Notice = string.Empty,
                Status = isLive,
                DanmakuData = isLive ? new TwitchDanmakuArgs()
                {
                    Channel = channel,
                    ViewerCountProvider = () => GetViewerCountAsync(channel),
                } : null,
                Url = BuildChannelUrl(channel),
            };
        }

        public async Task<LiveAudienceMetrics> GetDeferredAudienceMetrics(LiveRoomDetail roomDetail)
        {
            if (roomDetail == null)
            {
                return null;
            }
            var channel = NormalizeChannel(roomDetail.RoomID);
            return new LiveAudienceMetrics()
            {
                ViewerCount = await GetViewerCountAsync(channel),
                ViewerCountSource = "Twitch GQL stream.viewersCount",
                VipCount = null,
                VipCountSource = null,
                AllowPopularityFallback = false,
            };
        }

        public async Task<List<LivePlayQuality>> GetPlayQuality(LiveRoomDetail roomDetail)
        {
            if (roomDetail == null || !roomDetail.Status)
            {
                return new List<LivePlayQuality>();
            }

            var channel = NormalizeChannel(roomDetail.RoomID);
            var snapshot = await GetPlaybackSnapshotAsync(channel);
            return snapshot.Variants
                .Select((variant, index) => new LivePlayQuality()
                {
                    Quality = variant.Quality,
                    Sort = snapshot.Variants.Count - index,
                    Data = variant,
                })
                .ToList();
        }

        public async Task<List<string>> GetPlayUrls(LiveRoomDetail roomDetail, LivePlayQuality qn)
        {
            if (roomDetail == null || qn == null)
            {
                return new List<string>();
            }

            var channel = NormalizeChannel(roomDetail.RoomID);
            var variant = qn.Data as TwitchVariant;
            var cached = GetCachedPlaybackSnapshot(channel);
            if (variant != null
                && cached != null
                && cached.Variants.Contains(variant)
                && !string.IsNullOrWhiteSpace(variant.Url))
            {
                return new List<string>() { variant.Url };
            }

            var snapshot = await GetPlaybackSnapshotAsync(channel);
            var selected = variant == null
                ? snapshot.Variants.FirstOrDefault(x => string.Equals(x.Quality, qn.Quality, StringComparison.OrdinalIgnoreCase))
                : snapshot.Variants.FirstOrDefault(x => string.Equals(x.VariantId, variant.VariantId, StringComparison.OrdinalIgnoreCase));
            return selected == null || string.IsNullOrWhiteSpace(selected.Url)
                ? new List<string>()
                : new List<string>() { selected.Url };
        }

        public Task<List<LiveSuperChatMessage>> GetSuperChatMessages(object roomId)
        {
            return Task.FromResult(new List<LiveSuperChatMessage>());
        }

        public async Task<bool> GetLiveStatus(object roomId)
        {
            var channel = NormalizeChannel(roomId);
            var user = await GetChannelUserAsync(channel);
            return user?["stream"] is JObject;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler()
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }

        private async Task<JObject> GetChannelUserAsync(string channel)
        {
            const string query =
                "query ChannelInfo($login: String!) { " +
                "user(login: $login) { " +
                "id login displayName description profileImageURL(width: 300) bannerImageURL " +
                "lastBroadcast { title } " +
                "stream { id title viewersCount game { id name } previewImageURL(width: 640, height: 360) } " +
                "} " +
                "}";

            var variables = new JObject();
            variables["login"] = channel;
            var payload = new JObject();
            payload["operationName"] = "ChannelInfo";
            payload["variables"] = variables;
            payload["query"] = query;
            var root = await SendGraphQlAsync(payload);
            var data = root["data"] as JObject;
            return data?["user"] as JObject;
        }

        private async Task<long?> GetViewerCountAsync(string channel)
        {
            const string query =
                "query ViewerCount($login: String!) { " +
                "user(login: $login) { stream { viewersCount } } " +
                "}";

            var variables = new JObject();
            variables["login"] = channel;
            var payload = new JObject();
            payload["operationName"] = "ViewerCount";
            payload["variables"] = variables;
            payload["query"] = query;
            var root = await SendGraphQlAsync(payload);
            var data = root["data"] as JObject;
            var user = data?["user"] as JObject;
            var stream = user?["stream"] as JObject;
            return ReadLong(stream?["viewersCount"]);
        }

        private async Task<TwitchAccessToken> GetPlaybackAccessTokenAsync(string channel)
        {
            var variables = new JObject();
            variables["isLive"] = true;
            variables["login"] = channel;
            variables["isVod"] = false;
            variables["vodID"] = string.Empty;
            variables["playerType"] = "embed";
            variables["platform"] = "site";

            var persistedQuery = new JObject();
            persistedQuery["version"] = 1;
            persistedQuery["sha256Hash"] = PlaybackAccessTokenHash;
            var extensions = new JObject();
            extensions["persistedQuery"] = persistedQuery;

            var payload = new JObject();
            payload["operationName"] = "PlaybackAccessToken";
            payload["variables"] = variables;
            payload["extensions"] = extensions;

            var root = await SendGraphQlAsync(payload);
            var data = root["data"] as JObject;
            var accessToken = data?["streamPlaybackAccessToken"] as JObject;
            var signature = ReadString(accessToken?["signature"]);
            var value = ReadString(accessToken?["value"]);
            if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException("Twitch未返回有效的播放授权令牌");
            }

            return new TwitchAccessToken()
            {
                Signature = signature,
                Value = value,
            };
        }

        private async Task<JObject> SendGraphQlAsync(JObject payload)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl))
            {
                request.Headers.TryAddWithoutValidation("Client-ID", ClientId);
                request.Headers.TryAddWithoutValidation("X-Device-Id", DeviceId);
                request.Headers.TryAddWithoutValidation("Origin", TwitchOrigin);
                request.Headers.TryAddWithoutValidation("Referer", TwitchOrigin + "/");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using (var response = await HttpClient.SendAsync(request))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Twitch GQL请求失败（HTTP {(int)response.StatusCode}）");
                    }

                    JObject root;
                    try
                    {
                        root = JObject.Parse(text);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Twitch GQL返回了无法解析的数据", ex);
                    }

                    var errors = root["errors"] as JArray;
                    if (errors != null && errors.Count > 0)
                    {
                        var message = ReadString(errors[0]?["message"]);
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            message = "未知错误";
                        }
                        if (message.Length > 240)
                        {
                            message = message.Substring(0, 240);
                        }
                        throw new InvalidOperationException($"Twitch GQL请求失败：{message}");
                    }

                    return root;
                }
            }
        }

        private async Task<TwitchPlaybackSnapshot> GetPlaybackSnapshotAsync(string channel)
        {
            var cached = GetCachedPlaybackSnapshot(channel);
            if (cached != null)
            {
                return cached;
            }

            var accessToken = await GetPlaybackAccessTokenAsync(channel);
            var variants = await GetHlsVariantsAsync(channel, accessToken);
            if (variants.Count == 0)
            {
                throw new InvalidOperationException("Twitch未返回可播放的H.264清晰度");
            }

            var snapshot = new TwitchPlaybackSnapshot()
            {
                Channel = channel,
                Variants = variants,
                ExpiresAtUtc = DateTimeOffset.UtcNow.Add(PlaybackCacheLifetime),
            };
            lock (playbackCacheLock)
            {
                playbackCache = snapshot;
            }
            return snapshot;
        }

        private TwitchPlaybackSnapshot GetCachedPlaybackSnapshot(string channel)
        {
            lock (playbackCacheLock)
            {
                if (playbackCache == null
                    || !string.Equals(playbackCache.Channel, channel, StringComparison.OrdinalIgnoreCase)
                    || playbackCache.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    return null;
                }
                return playbackCache;
            }
        }

        private async Task<List<TwitchVariant>> GetHlsVariantsAsync(string channel, TwitchAccessToken accessToken)
        {
            var p = (long)(DateTime.UtcNow.Ticks % 999999L);
            var url = UsherBaseUrl + "/api/v2/channel/hls/" + Uri.EscapeDataString(channel) + ".m3u8"
                + "?platform=web"
                + "&p=" + p.ToString(CultureInfo.InvariantCulture)
                + "&allow_source=true"
                + "&allow_audio_only=true"
                + "&playlist_include_framerate=true"
                + "&supported_codecs=h264"
                + "&sig=" + Uri.EscapeDataString(accessToken.Signature)
                + "&token=" + Uri.EscapeDataString(accessToken.Value);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.apple.mpegurl, application/x-mpegURL, */*");
                request.Headers.TryAddWithoutValidation("Origin", TwitchOrigin);
                request.Headers.TryAddWithoutValidation("Referer", TwitchOrigin + "/");

                using (var response = await HttpClient.SendAsync(request))
                {
                    var playlist = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException($"Twitch HLS主清单请求失败（HTTP {(int)response.StatusCode}）");
                    }
                    if (string.IsNullOrWhiteSpace(playlist)
                        || playlist.IndexOf("#EXTM3U", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        throw new InvalidOperationException("Twitch返回的HLS主清单无效");
                    }
                    return ParseHlsVariants(playlist, url, channel);
                }
            }
        }

        private static List<TwitchVariant> ParseHlsVariants(string playlist, string masterUrl, string channel)
        {
            var result = new List<TwitchVariant>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = (playlist ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            Uri masterUri;
            if (!Uri.TryCreate(masterUrl, UriKind.Absolute, out masterUri))
            {
                return result;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var attributes = ParseHlsAttributes(line.Substring(line.IndexOf(':') + 1));
                string variantUrl = null;
                var urlIndex = i + 1;
                while (urlIndex < lines.Count)
                {
                    if (!lines[urlIndex].StartsWith("#", StringComparison.Ordinal))
                    {
                        variantUrl = lines[urlIndex];
                        break;
                    }
                    urlIndex++;
                }
                if (string.IsNullOrWhiteSpace(variantUrl))
                {
                    continue;
                }
                i = urlIndex;

                Uri resolvedUri;
                if (!Uri.TryCreate(masterUri, variantUrl, out resolvedUri))
                {
                    continue;
                }

                var codec = GetAttribute(attributes, "CODECS");
                var variantName = GetAttribute(attributes, "IVS-NAME");
                var stableId = GetAttribute(attributes, "STABLE-VARIANT-ID");
                var source = GetAttribute(attributes, "IVS-VARIANT-SOURCE");
                var resolution = GetAttribute(attributes, "RESOLUTION");
                var width = 0;
                var height = 0;
                ParseResolution(resolution, out width, out height);
                var frameRate = ParseDouble(GetAttribute(attributes, "FRAME-RATE"));
                var bandwidth = ParseLong(GetAttribute(attributes, "BANDWIDTH"));
                if (bandwidth <= 0)
                {
                    bandwidth = ParseLong(GetAttribute(attributes, "AVERAGE-BANDWIDTH"));
                }

                if (!IsH264VideoVariant(codec, variantName, stableId, width, height))
                {
                    continue;
                }
                var absoluteUrl = resolvedUri.AbsoluteUri;
                if (!seenUrls.Add(absoluteUrl))
                {
                    continue;
                }

                result.Add(new TwitchVariant()
                {
                    Channel = channel,
                    VariantId = string.IsNullOrWhiteSpace(stableId) ? absoluteUrl : stableId,
                    Quality = BuildQualityName(variantName, stableId, source, width, height, frameRate),
                    Url = absoluteUrl,
                    Codec = codec,
                    Source = source,
                    Width = width,
                    Height = height,
                    FrameRate = frameRate,
                    Bandwidth = bandwidth,
                    IsSource = string.Equals(source, "source", StringComparison.OrdinalIgnoreCase),
                });
            }

            return result
                .OrderByDescending(x => x.IsSource)
                .ThenByDescending(x => x.Width * x.Height)
                .ThenByDescending(x => x.FrameRate)
                .ThenByDescending(x => x.Bandwidth)
                .ToList();
        }

        private static bool IsH264VideoVariant(string codec, string name, string stableId, int width, int height)
        {
            var lowerCodec = (codec ?? string.Empty).ToLowerInvariant();
            var lowerName = (name ?? string.Empty).ToLowerInvariant();
            var lowerStableId = (stableId ?? string.Empty).ToLowerInvariant();
            if (lowerName == "audio_only" || lowerName == "audio"
                || lowerStableId == "audio_only" || lowerStableId == "audio")
            {
                return false;
            }
            if (lowerCodec.Contains("hev1") || lowerCodec.Contains("hvc1")
                || lowerCodec.Contains("av01") || lowerCodec.Contains("av1"))
            {
                return false;
            }
            if (lowerCodec.Contains("avc1") || lowerCodec.Contains("avc3") || lowerCodec.Contains("h264"))
            {
                return true;
            }
            // 旧版清单有时不带 CODECS，但视频变体仍会带分辨率。
            return string.IsNullOrWhiteSpace(codec) && width > 0 && height > 0;
        }

        private static string BuildQualityName(string name, string stableId, string source, int width, int height, double frameRate)
        {
            var quality = FirstNonEmpty(name, stableId);
            if (string.IsNullOrWhiteSpace(quality))
            {
                quality = height > 0 ? height.ToString(CultureInfo.InvariantCulture) + "p" : "Twitch线路";
            }
            if (frameRate >= 50 && !quality.Contains("60"))
            {
                quality += "60";
            }
            if (string.Equals(source, "source", StringComparison.OrdinalIgnoreCase)
                && !quality.StartsWith("原画", StringComparison.Ordinal))
            {
                quality = "原画 " + quality;
            }
            return quality;
        }

        private static Dictionary<string, string> ParseHlsAttributes(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            while (index < (value?.Length ?? 0))
            {
                while (index < value.Length && (value[index] == ',' || char.IsWhiteSpace(value[index])))
                {
                    index++;
                }
                if (index >= value.Length)
                {
                    break;
                }

                var equalsIndex = value.IndexOf('=', index);
                if (equalsIndex < 0)
                {
                    break;
                }
                var key = value.Substring(index, equalsIndex - index).Trim();
                index = equalsIndex + 1;
                string parsedValue;
                if (index < value.Length && value[index] == '"')
                {
                    index++;
                    var builder = new StringBuilder();
                    while (index < value.Length)
                    {
                        var current = value[index++];
                        if (current == '"')
                        {
                            break;
                        }
                        if (current == '\\' && index < value.Length)
                        {
                            builder.Append(value[index++]);
                        }
                        else
                        {
                            builder.Append(current);
                        }
                    }
                    parsedValue = builder.ToString();
                }
                else
                {
                    var commaIndex = value.IndexOf(',', index);
                    if (commaIndex < 0)
                    {
                        parsedValue = value.Substring(index).Trim();
                        index = value.Length;
                    }
                    else
                    {
                        parsedValue = value.Substring(index, commaIndex - index).Trim();
                        index = commaIndex + 1;
                    }
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = parsedValue;
                }
            }
            return result;
        }

        private static string GetAttribute(Dictionary<string, string> attributes, string name)
        {
            string value;
            return attributes != null && attributes.TryGetValue(name, out value) ? value : null;
        }

        private static void ParseResolution(string value, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            var match = Regex.Match(value.Trim(), @"^(\d+)x(\d+)$");
            if (!match.Success)
            {
                return;
            }
            int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
            int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
        }

        private static double ParseDouble(string value)
        {
            double result;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static long ParseLong(string value)
        {
            long result;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            return null;
        }

        private static string NormalizeChannel(object roomId)
        {
            var value = roomId?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Twitch频道名为空", nameof(roomId));
            }

            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                var host = uri.Host?.Trim().ToLowerInvariant();
                if (host == "twitch.tv" || host == "www.twitch.tv")
                {
                    var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    value = segments.Length == 1 ? Uri.UnescapeDataString(segments[0]) : string.Empty;
                }
            }

            value = value.Trim().Trim('/');
            if (!Regex.IsMatch(value, @"^[A-Za-z0-9_]{1,64}$"))
            {
                throw new ArgumentException("Twitch频道名格式无效", nameof(roomId));
            }
            return value.ToLowerInvariant();
        }

        private static string BuildChannelUrl(string channel)
        {
            return "https://www.twitch.tv/" + channel;
        }

        private static string ReadString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            return token.ToString();
        }

        private static long? ReadLong(JToken token)
        {
            var value = ReadString(token);
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (long?)null;
        }

        private static int ToCompatibleOnline(long? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return 0;
            }
            return value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;
        }

        private sealed class TwitchPlaybackSnapshot
        {
            public string Channel { get; set; }
            public List<TwitchVariant> Variants { get; set; }
            public DateTimeOffset ExpiresAtUtc { get; set; }
        }

        private sealed class TwitchAccessToken
        {
            public string Signature { get; set; }
            public string Value { get; set; }
        }

        private sealed class TwitchVariant
        {
            public string Channel { get; set; }
            public string VariantId { get; set; }
            public string Quality { get; set; }
            public string Url { get; set; }
            public string Codec { get; set; }
            public string Source { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public double FrameRate { get; set; }
            public long Bandwidth { get; set; }
            public bool IsSource { get; set; }
        }
    }
}
