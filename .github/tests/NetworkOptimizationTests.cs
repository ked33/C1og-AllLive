using AllLive.Core;
using AllLive.Core.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class NetworkOptimizationTests
    {
        public static async Task<string> RunAsync()
        {
            CheckQueryParameters();
            await CheckRedirectCookiesAndHeaderIsolationAsync();
            await CheckCrossHostRedirectAsync();
            await CheckCancellationReachesTransportAsync();
            await CheckFailureDisposesResponseAsync();
            await CheckRiskControlWaitCancellationAsync();
            return "PASS: HTTP redirect/cookie isolation, request cancellation, response disposal and risk-control cancellation.";
        }

        private static void CheckQueryParameters()
        {
            var url = HttpUtil.BuildUrl("https://example.test/path?first=1#anchor",
                new Dictionary<string, string> { { "query name", "a&b" }, { "empty", null } });
            Assert(url == "https://example.test/path?first=1&query%20name=a%26b&empty=#anchor", "Query parameters must preserve existing query and fragment.");
        }

        private static async Task CheckRedirectCookiesAndHeaderIsolationAsync()
        {
            var calls = 0;
            var requestHeaders = new Dictionary<string, string>
            {
                { "Cookie", "original=one" }, { "Authorization", "Bearer test" }, { "X-Account", "first" }
            };
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                calls++;
                if (calls == 1)
                {
                    Assert(request.Method == HttpMethod.Post, "Initial request should be POST.");
                    Assert(await request.Content.ReadAsStringAsync() == "payload", "POST body must survive buffering.");
                    requestHeaders["X-Account"] = "mutated-after-send";
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("/next", UriKind.Relative);
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "redirect_cookie=one; Path=/");
                    return response;
                }
                if (calls == 2)
                {
                    var cookies = string.Join(";", request.Headers.GetValues("Cookie"));
                    Assert(cookies.Contains("original=one") && cookies.Contains("redirect_cookie=one"), "Redirect must retain operation cookies.");
                    Assert(request.Method == HttpMethod.Get && request.Content == null, "302 POST redirect should switch to GET.");
                    Assert(!request.Headers.Contains("Authorization"), "Authorization must not be forwarded on redirect.");
                    Assert(request.Headers.GetValues("X-Account").Single() == "first", "Redirects must use the original header snapshot.");
                }
                else
                {
                    Assert(!request.Headers.Contains("Cookie"), "Cookies must not leak into the next independent request.");
                    Assert(request.Headers.GetValues("X-Account").Single() == "second", "Request headers must not be shared.");
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            })))
            {
                using (var body = new StringContent("payload"))
                using (var first = await HttpUtil.SendAsync(client, HttpMethod.Post, "https://example.test/start",
                    requestHeaders,
                    body, true, CancellationToken.None)) { }
                using (var second = await HttpUtil.SendAsync(client, HttpMethod.Get, "https://example.test/again",
                    new Dictionary<string, string> { { "X-Account", "second" } }, null, true, CancellationToken.None)) { }
                Assert(calls == 3, "The same client must serve both operations and the redirect.");
            }
        }

        private static async Task CheckCancellationReachesTransportAsync()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transportCancelled = false;
            using (var cancellation = new CancellationTokenSource())
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                started.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { transportCancelled = true; throw; }
                return new HttpResponseMessage(HttpStatusCode.OK);
            })))
            {
                var request = HttpUtil.SendAsync(client, HttpMethod.Get, "https://example.test/slow", null, null, true, cancellation.Token);
                await started.Task;
                cancellation.Cancel();
                try { using (await request) { } throw new InvalidOperationException("Cancelled HTTP request completed successfully."); }
                catch (OperationCanceledException) { }
                Assert(transportCancelled, "Cancellation must reach the transport, not just abandon its result.");
            }
        }

        private static async Task CheckCrossHostRedirectAsync()
        {
            var calls = 0;
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (++calls == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    response.Headers.Location = new Uri("https://other.test/receive");
                    return response;
                }
                Assert(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization"),
                    "Credentials must not cross hosts during redirects.");
                Assert(request.Method == HttpMethod.Post && await request.Content.ReadAsStringAsync() == "payload",
                    "307 redirect must preserve method and body.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
            })))
            using (var body = new StringContent("payload"))
            using (await HttpUtil.SendAsync(client, HttpMethod.Post, "https://example.test/start",
                new Dictionary<string, string> { { "Cookie", "private=one" }, { "Authorization", "Bearer test" } },
                body, true, CancellationToken.None))
            {
                Assert(calls == 2, "Cross-host redirect should complete once.");
            }
        }

        private static async Task CheckFailureDisposesResponseAsync()
        {
            var content = new TrackedContent();
            using (var client = new HttpClient(new Handler((request, token) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content }))))
            {
                try
                {
                    using (await HttpUtil.SendAsync(client, HttpMethod.Get, "https://example.test/error", null, null, true, CancellationToken.None)) { }
                    throw new InvalidOperationException("Non-success status should fail.");
                }
                catch (HttpRequestException) { }
                Assert(content.Disposed, "Failure must dispose the response body.");
            }
        }

        private static async Task CheckRiskControlWaitCancellationAsync()
        {
            DouyinRequestGate.ReportBlocked(DouyinBlockKind.Http444, "cancellation-test");
            using (var cancellation = new CancellationTokenSource())
            {
                var wait = DouyinRequestGate.WaitAsync(cancellation.Token);
                cancellation.Cancel();
                try { await wait; throw new InvalidOperationException("Cancelled gate wait completed successfully."); }
                catch (OperationCanceledException) { }
                Assert(DouyinRequestGate.IsPaused, "Cancelling one wait must not lift the shared risk-control pause.");
            }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
            public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { return send(request, cancellationToken); }
        }

        private sealed class TrackedContent : StringContent
        {
            public bool Disposed { get; private set; }
            public TrackedContent() : base("failure") { }
            protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
