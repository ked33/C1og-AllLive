using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace AllLive.Core.Helper
{
    internal static class BilibiliLocalFlvProxyClient
    {
        private const string DefaultBaseUrl = "http://127.0.0.1:8789";

        public static async Task<BilibiliLocalFlvProxyRegistrationResult> RegisterAsync(string upstreamUrl, IDictionary<string, string> requestHeaders)
        {
            if (string.IsNullOrWhiteSpace(upstreamUrl))
            {
                return BilibiliLocalFlvProxyRegistrationResult.Fail("upstream url empty");
            }

            var payload = new
            {
                url = upstreamUrl,
                referer = GetHeaderValue(requestHeaders, "referer", "https://live.bilibili.com/"),
                userAgent = GetHeaderValue(requestHeaders, "user-agent", GetDefaultUserAgent()),
                cookie = GetHeaderValue(requestHeaders, "cookie", string.Empty),
            };

            try
            {
                using (var handler = new HttpClientHandler()
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseProxy = false,
                })
                using (var client = new HttpClient(handler))
                {
                    // 这里只连接本机 SignService。代理是兼容回退，注册不可阻塞首帧主链路。
                    client.Timeout = TimeSpan.FromMilliseconds(750);
                    var json = JsonConvert.SerializeObject(payload);
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    using (var response = await client.PostAsync($"{DefaultBaseUrl}/api/bilibili/stream", content))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            return BilibiliLocalFlvProxyRegistrationResult.Fail($"http status={(int)response.StatusCode}");
                        }

                        var obj = JObject.Parse(body);
                        var code = obj["code"]?.ToObject<int>() ?? -1;
                        if (code != 0)
                        {
                            return BilibiliLocalFlvProxyRegistrationResult.Fail($"proxy code={code} msg={obj["msg"]}");
                        }

                        var proxyUrl = obj["data"]?["url"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(proxyUrl))
                        {
                            return BilibiliLocalFlvProxyRegistrationResult.Fail("proxy url empty");
                        }

                        return BilibiliLocalFlvProxyRegistrationResult.Ok(proxyUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                return BilibiliLocalFlvProxyRegistrationResult.Fail($"{ex.GetType().FullName}: {ex.Message}");
            }
        }

        private static string GetHeaderValue(IDictionary<string, string> headers, string name, string defaultValue)
        {
            if (headers == null || string.IsNullOrWhiteSpace(name))
            {
                return defaultValue;
            }

            foreach (var item in headers)
            {
                if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(item.Value) ? defaultValue : item.Value;
                }
            }

            return defaultValue;
        }

        private static string GetDefaultUserAgent()
        {
            return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
        }
    }

    internal sealed class BilibiliLocalFlvProxyRegistrationResult
    {
        public bool Success { get; private set; }
        public string Url { get; private set; }
        public string Error { get; private set; }

        private BilibiliLocalFlvProxyRegistrationResult(bool success, string url, string error)
        {
            Success = success;
            Url = url ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public static BilibiliLocalFlvProxyRegistrationResult Ok(string url)
        {
            return new BilibiliLocalFlvProxyRegistrationResult(true, url, string.Empty);
        }

        public static BilibiliLocalFlvProxyRegistrationResult Fail(string error)
        {
            return new BilibiliLocalFlvProxyRegistrationResult(false, string.Empty, error);
        }
    }
}
