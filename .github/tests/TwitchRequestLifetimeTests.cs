using AllLive.Core;
using AllLive.Core.Danmaku;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class TwitchRequestLifetimeTests
    {
        public static async Task<string> RunAsync()
        {
            using (var transport = new TwitchTransport())
            using (var client = new HttpClient(transport))
            using (var detailRequest = new CancellationTokenSource())
            {
                var twitch = new Twitch(client);
                var detail = await twitch.GetRoomDetail("example_channel", detailRequest.Token);
                var args = (TwitchDanmakuArgs)detail.DanmakuData;
                // Recovery may reuse this detail after its one-shot request was cancelled.
                detailRequest.Cancel();
                using (var firstConnection = new CancellationTokenSource())
                {
                    Assert(await args.ViewerCountProvider(firstConnection.Token) == 42,
                        "Viewer refresh must outlive the cancelled detail request.");
                    firstConnection.Cancel();
                }
                using (var recoveredConnection = new CancellationTokenSource())
                {
                    Assert(await args.ViewerCountProvider(recoveredConnection.Token) == 42,
                        "Reusing room detail must use the new connection token.");
                    transport.BlockViewerRequest = true;
                    var refresh = args.ViewerCountProvider(recoveredConnection.Token);
                    await transport.ViewerRequestStarted.Task;
                    recoveredConnection.Cancel();
                    try { await refresh; throw new InvalidOperationException("Cancelled viewer request succeeded."); }
                    catch (OperationCanceledException) { }
                    Assert(transport.TransportCancelled, "Stopping chat must cancel the active viewer request transport.");
                }
            }
            return "PASS: Twitch viewer refresh uses the current connection lifetime after playback recovery.";
        }

        private sealed class TwitchTransport : HttpMessageHandler
        {
            public bool BlockViewerRequest { get; set; }
            public bool TransportCancelled { get; private set; }
            public TaskCompletionSource<bool> ViewerRequestStarted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = JObject.Parse(await request.Content.ReadAsStringAsync());
                var operation = (string)payload["operationName"];
                if (operation == "ViewerCount" && BlockViewerRequest)
                {
                    ViewerRequestStarted.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    catch (OperationCanceledException) { TransportCancelled = true; throw; }
                }
                Assert(operation == "ChannelInfo" || operation == "ViewerCount", "Unexpected Twitch operation.");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"user\":{\"displayName\":\"Example\",\"stream\":{\"id\":\"1\",\"title\":\"Live\",\"viewersCount\":42}}}}")
                };
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
