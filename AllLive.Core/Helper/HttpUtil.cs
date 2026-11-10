using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Core.Helper
{
    public static class HttpUtil
    {
        // Reuse connections, but keep cookies and credentials scoped to each operation.
        private static readonly HttpClient compressedClient = CreateClient(true);
        private static readonly HttpClient plainClient = CreateClient(false);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private const int MaxRedirects = 10;

        private static HttpClient CreateClient(bool decompress)
        {
            return new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = decompress
                    ? DecompressionMethods.GZip | DecompressionMethods.Deflate
                    : DecompressionMethods.None
            }) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static async Task<string> GetString(string url, IDictionary<string, string> headers = null,
            IDictionary<string, string> queryParameters = null, bool ensureSuccess = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var response = await Get(url, headers, queryParameters, ensureSuccess, cancellationToken).ConfigureAwait(false))
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        // The caller owns the returned, fully buffered response.
        public static Task<HttpResponseMessage> Get(string url, IDictionary<string, string> headers = null,
            IDictionary<string, string> queryParameters = null, bool ensureSuccess = true,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(compressedClient, HttpMethod.Get, BuildUrl(url, queryParameters), headers,
                null, ensureSuccess, cancellationToken);
        }

        public static async Task<string> GetUtf8String(string url, IDictionary<string, string> headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var response = await SendAsync(plainClient, HttpMethod.Get, url, headers, null, true, cancellationToken).ConfigureAwait(false))
                return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }

        public static async Task<string> PostString(string url, string data, IDictionary<string, string> headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new List<KeyValuePair<string, string>>();
            foreach (var item in (data ?? string.Empty).Split('&'))
            {
                if (string.IsNullOrEmpty(item)) continue;
                var index = item.IndexOf('=');
                var key = index < 0 ? item : item.Substring(0, index);
                if (string.IsNullOrEmpty(key)) continue;
                body.Add(new KeyValuePair<string, string>(key, index < 0 ? string.Empty : item.Substring(index + 1)));
            }
            using (var content = new FormUrlEncodedContent(body))
            using (var response = await SendAsync(plainClient, HttpMethod.Post, url, headers, content, true, cancellationToken).ConfigureAwait(false))
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public static async Task<string> PostJsonString(string url, string data, IDictionary<string, string> headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var content = new StringContent(data, Encoding.UTF8, "application/json"))
            using (var response = await SendAsync(plainClient, HttpMethod.Post, url, headers, content, true, cancellationToken).ConfigureAwait(false))
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        public static Task<HttpResponseMessage> Head(string url, IDictionary<string, string> headers = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(compressedClient, HttpMethod.Head, url, headers, null, false, cancellationToken);
        }

        internal static string BuildUrl(string url, IDictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0) return url;
            var fragmentIndex = url.IndexOf('#');
            var fragment = fragmentIndex >= 0 ? url.Substring(fragmentIndex) : string.Empty;
            var address = fragmentIndex >= 0 ? url.Substring(0, fragmentIndex) : url;
            var builder = new StringBuilder(address);
            foreach (var pair in parameters)
            {
                var last = builder[builder.Length - 1];
                if (last != '?' && last != '&') builder.Append(address.IndexOf('?') >= 0 || builder.Length > address.Length ? '&' : '?');
                builder.Append(Uri.EscapeDataString(pair.Key));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }
            return builder.Append(fragment).ToString();
        }

        // Follow redirects with an operation-local jar, preserving Set-Cookie behavior without
        // sharing a CookieContainer between rooms/accounts. The injected client permits offline tests.
        internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url,
            IDictionary<string, string> headers, HttpContent content, bool ensureSuccess, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Redirects continue asynchronously; never re-read a caller-owned mutable dictionary.
            headers = headers == null ? null : new Dictionary<string, string>(headers);
            var uri = new Uri(url);
            var cookies = new CookieContainer();
            string originalCookie = null;
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (!string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                    originalCookie = header.Value;
                    foreach (var part in (header.Value ?? string.Empty).Split(';'))
                    {
                        var separator = part.IndexOf('=');
                        if (separator <= 0) continue;
                        try { cookies.Add(uri, new Cookie(part.Substring(0, separator).Trim(), part.Substring(separator + 1).Trim(), "/")); }
                        catch (CookieException) { }
                    }
                }
            }
            var body = content == null ? null : await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(RequestTimeout);
                for (var redirect = 0; ; redirect++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(method, uri))
                    {
                        if (body != null)
                        {
                            request.Content = new ByteArrayContent(body);
                            foreach (var header in content.Headers)
                                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                        if (headers != null)
                        {
                            foreach (var header in headers)
                            {
                                if (string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase)
                                    || redirect > 0 && (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))) continue;
                                request.Headers.Add(header.Key, header.Value);
                            }
                        }
                        var cookie = redirect == 0 && originalCookie != null ? originalCookie : cookies.GetCookieHeader(uri);
                        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
                        // Buffer the body while cancellation/timeout are active, not just the headers.
                        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                        try
                        {
                            IEnumerable<string> setCookies;
                            if (response.Headers.TryGetValues("Set-Cookie", out setCookies))
                                foreach (var value in setCookies)
                                    try { cookies.SetCookies(uri, value); } catch (CookieException) { }

                            var code = (int)response.StatusCode;
                            var location = response.Headers.Location;
                            if ((code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                                && location != null && redirect < MaxRedirects)
                            {
                                var target = location.IsAbsoluteUri ? location : new Uri(uri, location);
                                if ((target.Scheme == "http" || target.Scheme == "https")
                                    && !(uri.Scheme == "https" && target.Scheme == "http"))
                                {
                                    if ((code == 301 || code == 302) && method == HttpMethod.Post
                                        || code == 303 && method != HttpMethod.Head)
                                    {
                                        method = HttpMethod.Get;
                                        body = null;
                                    }
                                    uri = target;
                                    response.Dispose();
                                    continue;
                                }
                            }
                            if (ensureSuccess) response.EnsureSuccessStatusCode();
                            return response;
                        }
                        catch
                        {
                            response.Dispose();
                            throw;
                        }
                    }
                }
            }
        }
    }
}
