using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Helper
{
    public sealed class DouyuQrCode
    {
        public string Url { get; internal set; }
        public string Key { get; internal set; }
        public int ExpiresIn { get; internal set; }
    }

    public enum DouyuQrStatus { Pending, Scanned, Expired, Success }

    public sealed class DouyuQrResult
    {
        public DouyuQrStatus Status { get; internal set; }
        public string Cookie { get; internal set; }
    }

    // One instance per login/renewal. No shared cookie jar, no background polling timer.
    public sealed class DouyuAuthClient
    {
        internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
        private static readonly HttpClient sharedClient = new HttpClient(new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly HttpClient client;
        private readonly CookieContainer cookies = new CookieContainer(128, 64, 65536);
        private readonly string deviceId;

        public DouyuAuthClient(DouyuAccountSession session, bool renew = false) : this(sharedClient, session, renew) { }

        internal DouyuAuthClient(HttpClient client, DouyuAccountSession session, bool renew = false)
        {
            this.client = client;
            deviceId = session.DeviceId;
            var seed = renew ? session.Cookie : "dy_did=" + deviceId + "; acf_did=" + deviceId;
            foreach (var field in DouyuAccountSession.ParseCookies(seed))
                cookies.Add(new Cookie(field.Key, field.Value, "/", ".douyu.com") { Secure = true });
        }

        public async Task<DouyuQrCode> GenerateAsync(CancellationToken token)
        {
            var json = await SendAsync("https://passport.douyu.com/scan/generateCode",
                new Dictionary<string, string> { ["client_id"] = "1", ["isMultiAccount"] = "0" }, token).ConfigureAwait(false);
            RequireSuccess(json);
            var data = json["data"];
            var key = data?["code"]?.Value<string>();
            var url = data?["url"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("斗鱼未返回有效的二维码");
            ValidateDouyuUri(new Uri(url));
            var expires = data["expire"]?.Value<int?>() ?? 300;
            return new DouyuQrCode { Key = key, Url = url, ExpiresIn = expires > 0 ? Math.Min(expires, 600) : 300 };
        }

        public async Task<DouyuQrResult> PollAsync(string key, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("缺少扫码标识");
            var url = "https://passport.douyu.com/japi/scan/auth?time=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                "&code=" + Uri.EscapeDataString(key);
            var json = await SendAsync(url, null, token).ConfigureAwait(false);
            var code = json["error"]?.Value<int?>();
            if (code == -2) return new DouyuQrResult { Status = DouyuQrStatus.Pending };
            if (code == 1) return new DouyuQrResult { Status = DouyuQrStatus.Scanned };
            if (code == -1) return new DouyuQrResult { Status = DouyuQrStatus.Expired };
            RequireSuccess(json);
            var callback = (json["data"] as JObject)?["url"]?.Value<string>();
            var callbackUri = BuildCallbackUri(callback);
            var result = await SendAsync(callbackUri.AbsoluteUri, null, token).ConfigureAwait(false);
            RequireSuccess(result);
            var cookie = ExportCookie();
            if (!new DouyuAccountSession(cookie, deviceId, 0).HasLogin)
                throw new InvalidOperationException("未取得有效的斗鱼登录凭据，请重新扫码");
            token.ThrowIfCancellationRequested();
            return new DouyuQrResult { Status = DouyuQrStatus.Success, Cookie = cookie };
        }

        public async Task<string> RefreshAsync(CancellationToken token)
        {
            var result = await SendAsync("https://passport.douyu.com/lapi/passport/iframe/safeAuth?client_id=1", null, token).ConfigureAwait(false);
            RequireSuccess(result);
            var cookie = ExportCookie();
            if (!new DouyuAccountSession(cookie, deviceId, 0).HasLogin)
                throw new InvalidOperationException("斗鱼登录态续期未成功");
            return cookie;
        }

        internal static Uri BuildCallbackUri(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("斗鱼登录缺少回调地址");
            var uri = new Uri(new Uri("https://passport.douyu.com/"), raw);
            ValidateDouyuUri(uri);
            var builder = new UriBuilder(uri);
            builder.Query = (builder.Query.Length > 1 ? builder.Query.Substring(1) + "&" : "") +
                "callback=appClient_json_callback&_=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            builder.Fragment = "";
            return builder.Uri;
        }

        internal static void ValidateDouyuUri(Uri uri)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.Host.Equals("douyu.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".douyu.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("斗鱼登录返回了不受信任的地址");
        }

        private async Task<JObject> SendAsync(string url, Dictionary<string, string> form, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var uri = new Uri(url);
                for (var redirects = 0; ; redirects++)
                {
                    ValidateDouyuUri(uri);
                    using (var request = new HttpRequestMessage(form == null ? HttpMethod.Get : HttpMethod.Post, uri))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                        request.Headers.TryAddWithoutValidation("Origin", "https://passport.douyu.com");
                        request.Headers.Referrer = new Uri("https://passport.douyu.com/index/login?type=login&client_id=1");
                        request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
                        if (form != null) request.Content = new FormUrlEncodedContent(form);
                        using (var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                        {
                            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                                foreach (var value in values)
                                {
                                    try { cookies.SetCookies(uri, value); }
                                    catch (CookieException) { /* Ignore only malformed individual cookies. */ }
                                }
                            var status = (int)response.StatusCode;
                            if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                            {
                                if (redirects >= 5 || response.Headers.Location == null)
                                    throw new InvalidOperationException("斗鱼登录重定向失败");
                                uri = new Uri(uri, response.Headers.Location);
                                if (status == 301 || status == 302 || status == 303) form = null;
                                continue;
                            }
                            if (!response.IsSuccessStatusCode)
                                throw new InvalidOperationException("斗鱼登录服务暂不可用（HTTP " + status + "）");
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            timeout.Token.ThrowIfCancellationRequested();
                            if (text.Length > 1024 * 1024) throw new InvalidOperationException("斗鱼登录响应过大");
                            return ParseJsonp(text);
                        }
                    }
                }
            }
        }

        internal static JObject ParseJsonp(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end < start) throw new InvalidOperationException("斗鱼登录响应格式异常");
            return JObject.Parse(text.Substring(start, end - start + 1));
        }

        private string ExportCookie()
        {
            var fields = new Dictionary<string, string>();
            foreach (var host in new[] { "https://passport.douyu.com/", "https://www.douyu.com/" })
                foreach (Cookie cookie in cookies.GetCookies(new Uri(host)))
                    if (!cookie.Expired) fields[cookie.Name] = cookie.Value;
            fields["dy_did"] = fields.ContainsKey("dy_did") ? fields["dy_did"] : deviceId;
            fields["acf_did"] = fields["dy_did"];
            return DouyuAccountSession.FormatCookies(DouyuAccountSession.ParseCookies(DouyuAccountSession.FormatCookies(fields)));
        }

        private static void RequireSuccess(JObject json)
        {
            // Never include upstream body/msg/URLs in exceptions: they may contain credentials.
            var error = json["error"]?.Value<int?>();
            if (error != 0) throw new InvalidOperationException("斗鱼登录请求未成功，请重新扫码");
        }
    }
}
