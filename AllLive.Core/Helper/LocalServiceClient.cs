using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Helper
{
    // Only the built-in defaults are redirected. User-supplied remote endpoints remain unchanged.
    public static class LocalServiceClient
    {
        public static Func<Task<LocalServiceConnection>> ConnectionFactory { get; set; }
        public static Action<string> ConnectionFailed { get; set; }
        private static readonly Lazy<HttpClient> localClient = new Lazy<HttpClient>(() =>
            new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan });

        public static bool IsBuiltInUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "http"
                || (uri.Host != "127.0.0.1" && uri.Host != "localhost")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;
            return (uri.Port == 8788 && (uri.AbsolutePath == "/api/douyu/sign" || uri.AbsolutePath == "/api/douyin/sign"))
                || (uri.Port == 8789 && uri.AbsolutePath == "/api/bilibili/stream");
        }

        public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, HttpContent content)
        {
            return SendAsync(client, url, HttpMethod.Post, content, false);
        }

        public static Task<HttpResponseMessage> GetHealthAsync(HttpClient client, string serviceUrl)
        {
            return SendAsync(client, serviceUrl, HttpMethod.Get, null, true);
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string url,
            HttpMethod method, HttpContent content, bool health)
        {
            var factory = ConnectionFactory;
            var connection = factory != null && IsBuiltInUrl(url) ? await factory().ConfigureAwait(false) : null;
            var original = new Uri(url);
            var target = connection == null ? original : new Uri(connection.BaseUrl + original.AbsolutePath);
            if (health) target = new Uri(target.GetLeftPart(UriPartial.Authority) + "/health");
            using (var request = new HttpRequestMessage(method, target) { Content = content })
            using (var timeout = new CancellationTokenSource())
            {
                if (connection == null) return await client.SendAsync(request).ConfigureAwait(false);
                request.Headers.Add("X-AllLive-Session", connection.SessionId);
                if (client.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(client.Timeout);
                try
                {
                    return await localClient.Value.SendAsync(request, timeout.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    ConnectionFailed?.Invoke(connection.SessionId);
                    throw;
                }
            }
        }
    }

    public sealed class LocalServiceConnection
    {
        public string BaseUrl { get; private set; }
        public string SessionId { get; private set; }
        public LocalServiceConnection(int port, string sessionId)
        {
            Guid parsed;
            if (port < 1 || port > 65535 || !Guid.TryParseExact(sessionId, "N", out parsed))
                throw new ArgumentException("Invalid local service handshake.");
            BaseUrl = "http://127.0.0.1:" + port;
            SessionId = sessionId;
        }
    }
}
