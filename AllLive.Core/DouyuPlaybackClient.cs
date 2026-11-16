using AllLive.Core.Helper;
using AllLive.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core
{
    internal sealed class DouyuQualityData
    {
        internal int Rate;
        internal long AccountRevision;
        internal List<string> Cdns;
        internal Dictionary<int, string> Names;
    }

    internal sealed class DouyuPlaybackClient
    {
        private static readonly HttpClient sharedClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly HttpClient client;
        private readonly SemaphoreSlim encryptionGate = new SemaphoreSlim(1, 1);
        private JObject encryption;
        private long encryptionRevision = -1;
        private DateTimeOffset fetchedAt;

        internal DouyuPlaybackClient() : this(sharedClient) { }
        internal DouyuPlaybackClient(HttpClient client) { this.client = client; }

        internal async Task<List<LivePlayQuality>> GetQualitiesAsync(string roomId, CancellationToken token)
        {
            try
            {
                var session = DouyuAccountState.Capture();
                var data = await RequestPlayAsync(roomId, -1, "", session, token).ConfigureAwait(false);
                EnsureCurrent(session, token);
                return ParseQualities(data, session.Revision);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                CoreDebug.Log(() => "[Douyu] 播放信息失败 error=" + ex.GetType().Name);
                throw new InvalidOperationException("斗鱼播放信息暂不可用，请刷新或检查登录状态");
            }
        }

        internal async Task<List<LivePlayUrl>> GetUrlsAsync(string roomId, LivePlayQuality quality, CancellationToken token)
        {
            var session = DouyuAccountState.Capture();
            var requested = quality.Data as DouyuQualityData;
            if (requested == null || requested.AccountRevision != session.Revision)
                throw new InvalidOperationException("斗鱼登录状态已变化，请刷新直播间");
            using (var slots = new SemaphoreSlim(2, 2))
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                var tasks = requested.Cdns.Select(async cdn =>
                {
                    var acquired = false;
                    try
                    {
                        await slots.WaitAsync(budget.Token).ConfigureAwait(false);
                        acquired = true;
                        using (var lineTimeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
                        {
                            lineTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                            var data = await RequestPlayAsync(roomId, requested.Rate, cdn, session, lineTimeout.Token).ConfigureAwait(false);
                            var result = ParseUrl(data, requested.Rate, requested.Names);
                            if (result != null) result.RequestedQuality = quality.Quality;
                            return result;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        CoreDebug.Log(() => "[Douyu] 线路解析失败 error=" + ex.GetType().Name);
                        return null;
                    }
                    finally { if (acquired) slots.Release(); }
                }).ToArray();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                EnsureCurrent(session, token);
                var selected = SelectQualityGroup(results, requested.Rate);
                CoreDebug.Log(() => "[Douyu] 画质确认 requested=" + requested.Rate +
                    " applied=" + (selected.FirstOrDefault()?.AppliedRate?.ToString(CultureInfo.InvariantCulture) ?? "unknown") +
                    " urls=" + selected.Count + " authenticated=" + session.HasLogin);
                return selected;
            }
        }

        internal static List<LivePlayUrl> SelectQualityGroup(IEnumerable<LivePlayUrl> urls, int requestedRate)
        {
            var candidates = urls.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Url)).ToList();
            var applied = candidates.FirstOrDefault(x => x.AppliedRate == requestedRate)?.AppliedRate ??
                candidates.FirstOrDefault(x => x.AppliedRate.HasValue)?.AppliedRate;
            // Keep same-quality fallback URLs together. Missing rate never implies "original".
            return candidates.Where(x => x.AppliedRate == applied).GroupBy(x => x.Url, StringComparer.Ordinal)
                .Select(x => x.First()).ToList();
        }

        internal static List<LivePlayQuality> ParseQualities(JObject data, long revision)
        {
            var names = new Dictionary<int, string>();
            var rates = new List<int>();
            foreach (var item in data["multirates"] as JArray ?? new JArray())
            {
                var rate = ReadRate(item["rate"]);
                if (!rate.HasValue || names.ContainsKey(rate.Value)) continue;
                rates.Add(rate.Value);
                names[rate.Value] = item["name"]?.Value<string>()?.Trim() ?? "";
                if (names[rate.Value].Length == 0) names[rate.Value] = "档位 " + rate.Value;
            }
            if (rates.Count == 0)
            {
                var rate = ReadRate(data["rate"]) ?? -1;
                rates.Add(rate);
                names[rate] = "默认";
            }
            var cdns = (data["cdnsWithName"] as JArray ?? new JArray())
                .Select(x => x["cdn"]?.Value<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal).ToList();
            var current = data["rtmp_cdn"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(current) && !cdns.Contains(current)) cdns.Insert(0, current);
            if (cdns.Count == 0) cdns.Add("");
            cdns = cdns.OrderBy(x => x.StartsWith("scdn", StringComparison.OrdinalIgnoreCase)).ToList();
            return rates.Select(rate => new LivePlayQuality
            {
                Id = rate.ToString(CultureInfo.InvariantCulture), Quality = names[rate],
                Data = new DouyuQualityData { Rate = rate, Cdns = cdns, Names = names, AccountRevision = revision }
            }).ToList();
        }

        internal static LivePlayUrl ParseUrl(JObject data, int requestedRate, IDictionary<int, string> names)
        {
            var root = data["rtmp_url"]?.Value<string>();
            var path = data["rtmp_live"]?.Value<string>();
            var url = !string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(path)
                ? root.TrimEnd('/') + "/" + path.TrimStart('/') :
                new[] { "flv_url", "player_1", "stream_url", "url" }.Select(k => data[k])
                    .FirstOrDefault(x => x?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(x.Value<string>()))?.Value<string>();
            url = WebUtility.HtmlDecode(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0) return null;
            var applied = ReadRate(data["rate"]);
            string label = null;
            if (applied.HasValue)
            {
                names.TryGetValue(applied.Value, out label);
                var actual = (data["multirates"] as JArray)?.FirstOrDefault(x => ReadRate(x["rate"]) == applied);
                if (!string.IsNullOrWhiteSpace(actual?["name"]?.Value<string>())) label = actual["name"].Value<string>();
            }
            return new LivePlayUrl { Url = uri.AbsoluteUri, RequestedRate = requestedRate, AppliedRate = applied, AppliedQuality = label };
        }

        internal static int? ReadRate(JToken value)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float && value.Type != JTokenType.String)) return null;
            if (!decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                number < 0 || number > int.MaxValue || decimal.Truncate(number) != number) return null;
            return (int)number;
        }

        private async Task<JObject> RequestPlayAsync(string roomId, int rate, string cdn, DouyuAccountSession session, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(roomId) || !roomId.All(c => c >= '0' && c <= '9'))
                throw new ArgumentException("斗鱼房间号无效");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureCurrent(session, token);
                try
                {
                    var descriptor = await GetEncryptionAsync(session, attempt > 0, token).ConfigureAwait(false);
                    var form = BuildForm(descriptor, session.DeviceId, roomId, rate, cdn, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    using (var content = new FormUrlEncodedContent(form))
                    using (var response = await HttpUtil.SendAsync(client, HttpMethod.Post,
                        "https://www.douyu.com/lapi/live/getH5PlayV1/" + roomId, Headers(session, roomId), content, true, token).ConfigureAwait(false))
                    {
                        var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                        if ((json["error"]?.Value<int?>() ?? json["code"]?.Value<int?>()) != 0 ||
                            (json["code"]?.Value<int?>() ?? 0) != 0 || !(json["data"] is JObject data))
                            throw new InvalidOperationException("斗鱼未返回有效的播放信息");
                        EnsureCurrent(session, token);
                        return data;
                    }
                }
                catch (Exception ex) when (attempt == 0 && !(ex is OperationCanceledException)) { }
            }
            throw new InvalidOperationException("斗鱼播放信息请求失败");
        }

        private async Task<JObject> GetEncryptionAsync(DouyuAccountSession session, bool force, CancellationToken token)
        {
            await encryptionGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureCurrent(session, token);
                var now = DateTimeOffset.UtcNow;
                if (!force && encryption != null && encryptionRevision == session.Revision &&
                    now - fetchedAt < TimeSpan.FromMinutes(5) && (encryption["expire_at"]?.Value<long?>() ?? 0) > now.ToUnixTimeSeconds() + 30)
                    return encryption;
                using (var response = await HttpUtil.SendAsync(client, HttpMethod.Get,
                    "https://www.douyu.com/wgapi/livenc/liveweb/websec/getEncryption?did=" + Uri.EscapeDataString(session.DeviceId),
                    Headers(session, ""), null, true, token).ConfigureAwait(false))
                {
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var data = json["data"] as JObject;
                    var rounds = data?["enc_time"]?.Value<int?>() ?? 0;
                    if (json["error"]?.Value<int?>() != 0 || data == null || rounds < 1 || rounds > 16 ||
                        new[] { "key", "rand_str", "enc_data" }.Any(k => string.IsNullOrWhiteSpace(data[k]?.Value<string>())) ||
                        (data["expire_at"]?.Value<long?>() ?? 0) <= now.ToUnixTimeSeconds() + 30)
                        throw new InvalidOperationException("斗鱼签名描述符无效或已过期");
                    EnsureCurrent(session, token);
                    encryption = data;
                    encryptionRevision = session.Revision;
                    fetchedAt = now;
                    return data;
                }
            }
            finally { encryptionGate.Release(); }
        }

        internal static Dictionary<string, string> BuildForm(JObject descriptor, string did, string roomId, int rate, string cdn, long timestamp)
        {
            var key = descriptor["key"].Value<string>();
            var secret = descriptor["rand_str"].Value<string>();
            for (var i = 0; i < descriptor["enc_time"].Value<int>(); i++) secret = Md5(secret + key);
            var time = timestamp.ToString(CultureInfo.InvariantCulture);
            var suffix = descriptor["is_special"]?.Value<int?>() == 1 ? "" : roomId + time;
            return new Dictionary<string, string>
            {
                ["enc_data"] = descriptor["enc_data"].Value<string>(), ["tt"] = time, ["did"] = did,
                ["auth"] = Md5(secret + key + suffix), ["cdn"] = cdn ?? "", ["rate"] = rate.ToString(CultureInfo.InvariantCulture),
                ["hevc"] = "0", ["fa"] = "0", ["ive"] = "0", ["ver"] = "Douyu_new", ["iar"] = "0"
            };
        }

        private static string Md5(string value)
        {
            using (var hash = MD5.Create())
                return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static Dictionary<string, string> Headers(DouyuAccountSession session, string roomId) => new Dictionary<string, string>
        {
            ["User-Agent"] = DouyuAuthClient.UserAgent, ["Accept"] = "application/json, text/plain, */*",
            ["Origin"] = "https://www.douyu.com", ["Referer"] = "https://www.douyu.com/" + roomId,
            ["Cookie"] = session.PlaybackCookie
        };

        private static void EnsureCurrent(DouyuAccountSession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!DouyuAccountState.IsCurrent(session)) throw new OperationCanceledException("斗鱼登录状态已变化");
        }
    }
}
