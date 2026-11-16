using AllLive.Core;
using AllLive.Core.Helper;
using AllLive.Core.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AllLive.Tests
{
    public static class DouyuAccountTests
    {
        public static async Task<string> RunAsync()
        {
            CoreConfig.SetDouyuDeviceId("anonymous-device");
            CheckCookieIsolation();
            CheckQualityContracts();
            await CheckQrLoginAsync();
            await CheckAuthRedirectAndCancellationAsync();
            await CheckRenewalAndExpiryAsync();
            await CheckPlaybackAsync();
            await CheckQualityMenuAsync();
            await CheckQualityProbeCancellationAsync();
            await CheckBoundedRetryAsync();
            await CheckAccountChangeDuringRequestAsync();
            DouyuAccountState.SetCookie("");
            return "PASS: Douyu login protocol, cookie/device isolation, callback restrictions, cancellation, V1 signing/cache and actual quality grouping.";
        }

        private static void CheckCookieIsolation()
        {
            DouyuAccountState.SetCookie("dy_auth=web; acf_auth=h5; acf_jwt_token=jwt; LTP0=renew; dy_did=login-device; acf_uid=123; acf_nickname=%E6%B5%8B%E8%AF%95");
            var loggedIn = DouyuAccountState.Capture();
            Assert(loggedIn.HasLogin && loggedIn.DeviceId == "login-device" && loggedIn.UserName == "测试", "Login identity was lost.");
            var cookies = DouyuAccountSession.ParseCookies(loggedIn.PlaybackCookie);
            Assert(!cookies.ContainsKey("dy_auth") && !cookies.ContainsKey("LTP0"), "Web or renewal credential leaked to H5.");
            Assert(cookies["dy_did"] == cookies["acf_did"] && cookies["acf_jwt_token"] == "jwt", "Device/H5 credentials differ.");
            DouyuAccountState.SetCookie("dy_auth=web; acf_auth=incomplete");
            cookies = DouyuAccountSession.ParseCookies(DouyuAccountState.Capture().PlaybackCookie);
            Assert(cookies.ContainsKey("dy_auth") && !cookies.ContainsKey("acf_auth"), "Incomplete H5 pair was sent.");
            Assert(!DouyuAccountSession.ContainsLogin("acf_auth=alone"), "Partial login was accepted.");
            DouyuAccountState.SetCookie("");
            Assert(!DouyuAccountState.Capture().HasLogin && DouyuAccountState.Capture().DeviceId == "anonymous-device", "Logout retained login/device state.");
            Assert(!DouyuAccountState.IsCurrent(loggedIn), "Stale login snapshot is current.");
            Throws(() => DouyuAccountSession.ParseCookies("dy_auth=x\r\nInjected: y"), "Header injection was accepted.");
        }

        private static void CheckQualityContracts()
        {
            var data = JObject.Parse("{ 'multirates': [{ 'rate': 0, 'name': '原画' }, { 'rate': '4', 'name': '蓝光4M' }, { 'rate': 4, 'name': '重复' }], 'cdnsWithName': [{ 'cdn': 'scdn' }, { 'cdn': 'ws' }], 'rtmp_cdn': 'hw' }");
            var qualities = DouyuPlaybackClient.ParseQualities(data, 10);
            Assert(qualities.Count == 2 && qualities[0].Id == "0" && qualities[1].Quality == "蓝光4M", "Platform quality order/names changed.");
            var q = (DouyuQualityData)qualities[0].Data;
            Assert(q.Cdns.SequenceEqual(new[] { "hw", "ws", "scdn" }) && q.AccountRevision == 10, "CDN order or account revision was lost.");
            Assert(DouyuPlaybackClient.ReadRate(new JValue("0")) == 0 && DouyuPlaybackClient.ReadRate(new JValue(4.0)) == 4, "Integral rate was rejected.");
            foreach (var token in new JToken[] { null, JValue.CreateNull(), new JValue(-1), new JValue(4.5), new JValue(""), new JValue(true), new JValue("2147483648"), new JObject() })
                Assert(DouyuPlaybackClient.ReadRate(token) == null, "Invalid/missing rate was interpreted as original.");
            var lower = DouyuPlaybackClient.ParseUrl(JObject.Parse("{ 'rtmp_url':'https://cdn.example', 'rtmp_live':'low.flv?a=1&amp;b=2', 'rate':'4' }"), 0, q.Names);
            Assert(lower.AppliedRate == 4 && lower.AppliedQuality == "蓝光4M" && lower.Url.EndsWith("a=1&b=2"), "Downshift metadata was lost.");
            var original = new LivePlayUrl { Url = "https://cdn.example/original.flv", AppliedRate = 0 };
            var other = new LivePlayUrl { Url = "https://other.example/low.flv", AppliedRate = 4 };
            var unknown = new LivePlayUrl { Url = "https://cdn.example/unknown.flv" };
            Assert(DouyuPlaybackClient.SelectQualityGroup(new[] { lower, original, unknown }, 0).Single() == original, "Requested confirmed rate was not preferred.");
            Assert(DouyuPlaybackClient.SelectQualityGroup(new[] { lower, other, unknown }, 0).Count == 2, "Fallback mixed unknown/different rates.");
            Assert(DouyuPlaybackClient.SelectQualityGroup(new[] { unknown }, 0).Single().AppliedRate == null, "Unknown fallback became original.");
            Assert(DouyuPlaybackClient.ParseUrl(JObject.Parse("{'url':'file:///C:/x','rate':0}"), 0, q.Names) == null, "Non-network playback URL was accepted.");
        }

        private static async Task CheckQrLoginAsync()
        {
            DouyuAccountState.SetCookie("");
            var session = DouyuAccountState.Capture();
            var polls = 0;
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                var header = string.Join(";", request.Headers.GetValues("Cookie"));
                Assert(header.Contains("dy_did=anonymous-device"), "QR session lost the signing device.");
                if (request.RequestUri.AbsolutePath.EndsWith("generateCode"))
                {
                    Assert(request.Method == HttpMethod.Post && (await request.Content.ReadAsStringAsync()).Contains("client_id=1"), "QR generation form is wrong.");
                    return Json("{'error':0,'data':{'code':'qr-key','url':'https://m.douyu.com/scan/test','expire':60}}");
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/auth"))
                {
                    polls++;
                    if (polls == 1) return Json("{'error':-2}");
                    if (polls == 2) return Json("{'error':1}");
                    var response = Json("{'error':0,'data':{'url':'/callback'}}");
                    response.Headers.TryAddWithoutValidation("Set-Cookie", "acf_auth=h5; Domain=.douyu.com; Path=/; Secure");
                    return response;
                }
                Assert(request.RequestUri.Query.Contains("callback=appClient_json_callback"), "JSONP callback missing.");
                var final = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("appClient_json_callback({\"error\":0});") };
                final.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
                    "acf_jwt_token=jwt; Domain=.douyu.com; Path=/; Secure",
                    "dy_auth=web; Domain=.douyu.com; Path=/; Secure",
                    "LTP0=renewal; Domain=.douyu.com; Path=/; Secure" });
                return final;
            })))
            {
                var auth = new DouyuAuthClient(client, session);
                var qr = await auth.GenerateAsync(CancellationToken.None);
                Assert(qr.ExpiresIn == 60, "QR expiry was lost.");
                Assert((await auth.PollAsync(qr.Key, CancellationToken.None)).Status == DouyuQrStatus.Pending, "Pending state wrong.");
                Assert((await auth.PollAsync(qr.Key, CancellationToken.None)).Status == DouyuQrStatus.Scanned, "Scanned state wrong.");
                var result = await auth.PollAsync(qr.Key, CancellationToken.None);
                Assert(result.Status == DouyuQrStatus.Success && DouyuAccountSession.ContainsLogin(result.Cookie), "Callback cookies were lost.");
                Assert(DouyuAccountState.IsCurrent(session) && !session.HasLogin, "Protocol polling persisted login before UI ownership check.");
            }
        }

        private static async Task CheckAuthRedirectAndCancellationAsync()
        {
            foreach (var url in new[] { "https://douyu.com.evil.example/x", "https://evil.example/x", "http://passport.douyu.com/x", "https://user@passport.douyu.com/x", "https://passport.douyu.com:444/x" })
                Throws(() => DouyuAuthClient.BuildCallbackUri(url), "Untrusted callback accepted.");
            Assert(DouyuAuthClient.BuildCallbackUri("//passport.douyu.com/return?x=1").Query.Contains("x=1&callback="), "Relative callback query lost.");
            var calls = 0;
            using (var client = new HttpClient(new Handler((request, token) =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://evil.example/steal");
                return Task.FromResult(response);
            })))
            {
                await ThrowsAsync(() => new DouyuAuthClient(client, DouyuAccountState.Capture()).GenerateAsync(CancellationToken.None), "Untrusted redirect followed.");
                Assert(calls == 1, "Credentials were sent to redirect target.");
            }
            var entered = new TaskCompletionSource<bool>();
            using (var cancel = new CancellationTokenSource())
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return Json("{'error':0}");
            })))
            {
                var request = new DouyuAuthClient(client, DouyuAccountState.Capture()).PollAsync("key", cancel.Token);
                await entered.Task;
                cancel.Cancel();
                try { await request; throw new InvalidOperationException("Cancellation was ignored."); }
                catch (OperationCanceledException) { }
            }
        }

        private static JObject Descriptor() => new JObject
        {
            ["key"] = "key", ["rand_str"] = "seed", ["enc_time"] = 2,
            ["enc_data"] = "a+b/=&x", ["is_special"] = 0,
            ["expire_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600
        };

        private static async Task CheckRenewalAndExpiryAsync()
        {
            DouyuAccountState.SetCookie("dy_auth=old; dy_did=saved-device; LTP0=renewal");
            var session = DouyuAccountState.Capture();
            using (var client = new HttpClient(new Handler((request, token) =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/auth")) return Task.FromResult(Json("{'error':-1}"));
                Assert(string.Join(";", request.Headers.GetValues("Cookie")).Contains("LTP0=renewal"), "Passport renewal key was removed.");
                var response = Json("{'error':0}");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "dy_auth=new; Domain=.douyu.com; Path=/; Secure");
                return Task.FromResult(response);
            })))
            {
                var auth = new DouyuAuthClient(client, session, renew: true);
                Assert((await auth.PollAsync("expired", CancellationToken.None)).Status == DouyuQrStatus.Expired, "Expired QR did not stop.");
                var renewed = await auth.RefreshAsync(CancellationToken.None);
                Assert(renewed.Contains("dy_auth=new") && !renewed.Contains("dy_auth=old"), "Renewal kept stale cookie.");
                Assert(DouyuAccountState.IsCurrent(session), "Renewal committed before account ownership check.");
            }
            using (var client = new HttpClient(new Handler((request, token) =>
            {
                var response = Json("{'error':0}");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "dy_auth=; Domain=.douyu.com; Path=/; Max-Age=0");
                return Task.FromResult(response);
            })))
                await ThrowsAsync(() => new DouyuAuthClient(client, session, renew: true).RefreshAsync(CancellationToken.None), "Expired credentials survived cookie deletion.");
        }

        private static async Task CheckBoundedRetryAsync()
        {
            var descriptors = 0;
            var plays = 0;
            using (var client = new HttpClient(new Handler((request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    descriptors++;
                    return Task.FromResult(Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString()));
                }
                plays++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            })))
            {
                await ThrowsAsync(() => new DouyuPlaybackClient(client).GetQualitiesAsync("123", CancellationToken.None), "Failed playback was accepted.");
                Assert(descriptors == 2 && plays == 2, "Signature refresh/retry was not bounded to two attempts.");
            }
        }

        private static async Task CheckPlaybackAsync()
        {
            DouyuAccountState.SetCookie("dy_did=account-device; acf_auth=h5; acf_jwt_token=jwt; dy_auth=web; LTP0=renew");
            var encryptionCount = 0;
            var active = 0;
            var maxActive = 0;
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                var cookie = string.Join(";", request.Headers.GetValues("Cookie"));
                Assert(cookie.Contains("dy_did=account-device") && cookie.Contains("acf_jwt_token=jwt") && !cookie.Contains("dy_auth=") && !cookie.Contains("LTP0="), "V1 cookie policy failed.");
                if (request.Method == HttpMethod.Get)
                {
                    encryptionCount++;
                    return Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString());
                }
                var form = await request.Content.ReadAsStringAsync();
                Assert(form.Contains("enc_data=a%2Bb%2F%3D%26x") && !form.Contains("%252B"), "Signature data was double encoded.");
                Assert(form.Contains("did=account-device") && form.Contains("ver=Douyu_new"), "V1 identity/form mismatch.");
                var current = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, current);
                await Task.Delay(15, token);
                Interlocked.Decrement(ref active);
                return Json("{'error':0,'data':{'multirates':[{'rate':0,'name':'原画'},{'rate':4,'name':'蓝光4M'}], 'cdnsWithName':[{'cdn':'a'},{'cdn':'b'},{'cdn':'c'}], 'rtmp_url':'https://cdn.example', 'rtmp_live':'stream.flv', 'rate':4}}");
            })))
            {
                var playback = new DouyuPlaybackClient(client);
                var qualities = await playback.GetQualitiesAsync("123", CancellationToken.None);
                var urls = await playback.GetUrlsAsync("123", qualities[0], CancellationToken.None);
                Assert(encryptionCount == 1 && maxActive <= 2, "Descriptor cache or concurrency bound failed.");
                Assert(qualities.Count == 1 && qualities[0].Quality == "蓝光4M" && qualities[0].Id == "4", "Unavailable/duplicate tiers remained in the menu.");
                Assert(urls.Single().AppliedQuality == "蓝光4M" && urls[0].RequestedQuality == "蓝光4M", "URL lost requested/actual quality.");
                var signature = DouyuPlaybackClient.BuildForm(Descriptor(), "did", "123", 0, "", 1700000000);
                Assert(signature["auth"] == "10182213072cf7d9afe0daacd25abf7a", "V1 signature differs from known MD5 vector.");
                DouyuAccountState.SetCookie("");
                await ThrowsAsync(() => playback.GetUrlsAsync("123", qualities[0], CancellationToken.None), "Old-account quality data reused.");
            }
        }

        private static async Task CheckQualityMenuAsync()
        {
            DouyuAccountState.SetCookie("");
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString());
                var form = await request.Content.ReadAsStringAsync();
                var rate = form.Split('&').Single(x => x.StartsWith("rate=", StringComparison.Ordinal)).Substring(5);
                if (rate == "2") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var response = JObject.Parse("{'multirates':[{'rate':0,'name':'原画'},{'rate':4,'name':'蓝光4M'},{'rate':3,'name':'高清'},{'rate':2,'name':'超清'},{'rate':1,'name':'流畅'}], 'rtmp_url':'https://cdn.example', 'rtmp_live':'stream.flv'}");
                if (rate != "3") response["rate"] = rate == "1" ? 1 : 4;
                return Json(new JObject { ["error"] = 0, ["data"] = response }.ToString());
            })))
            {
                var playback = new DouyuPlaybackClient(client);
                var menu = await playback.GetQualitiesAsync("123", CancellationToken.None);
                Assert(menu.Select(x => x.Quality).SequenceEqual(new[] { "蓝光4M", "未确认", "流畅" }), "Menu used advertised/failed tiers or lost platform order.");
                var unknown = menu.Single(x => x.Quality == "未确认");
                var corrected = DouyuPlaybackClient.ApplyActualQuality(menu, unknown,
                    new LivePlayUrl { AppliedRate = 4, AppliedQuality = "蓝光4M" });
                Assert(menu.Count == 2 && menu.Contains(corrected) && corrected.Quality == "蓝光4M", "Late downshift did not merge the selected tier.");
                Assert(((DouyuQualityData)corrected.Data).Rate == 3 && corrected.Id == "3", "Display correction changed the real request identity.");
                Assert(unknown.Quality == "未确认", "An in-flight quality object was mutated.");
                corrected = DouyuPlaybackClient.ApplyActualQuality(menu, corrected, new LivePlayUrl());
                Assert(corrected.Quality == "未确认", "Missing actual rate kept a stale confirmed label.");
                corrected = DouyuPlaybackClient.ApplyActualQuality(menu, corrected, new LivePlayUrl { AppliedRate = 99 });
                Assert(corrected.Quality == "档位 99", "Unknown rate name borrowed the requested label.");
            }

            // If individual probes fail, the initial response remains usable, but
            // its actual rate must not be confused with the first advertised tier.
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                    return Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString());
                var form = await request.Content.ReadAsStringAsync();
                if (!form.Split('&').Contains("rate=-1")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return Json("{'error':0,'data':{'multirates':[{'rate':0,'name':'原画'},{'rate':4,'name':'蓝光4M'}], 'rtmp_url':'https://cdn.example', 'rtmp_live':'stream.flv', 'rate':4}}");
            })))
            {
                var playback = new DouyuPlaybackClient(client);
                var fallback = (await playback.GetQualitiesAsync("123", CancellationToken.None)).Single();
                Assert(fallback.Quality == "蓝光4M" && fallback.Id == "-1", "Failed probes lost the playable default response.");
                var info = (await playback.GetUrlsAsync("123", fallback, CancellationToken.None)).Single();
                Assert(info.RequestedRate == -1 && info.AppliedRate == 4, "Default fallback sent the wrong request rate.");
            }
        }

        private static async Task CheckQualityProbeCancellationAsync()
        {
            foreach (var changeAccount in new[] { false, true })
            {
                DouyuAccountState.SetCookie("");
                var entered = new TaskCompletionSource<bool>();
                var release = new TaskCompletionSource<bool>();
                using (var cancel = new CancellationTokenSource())
                using (var client = new HttpClient(new Handler(async (request, token) =>
                {
                    if (request.Method == HttpMethod.Get)
                        return Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString());
                    var form = await request.Content.ReadAsStringAsync();
                    if (!form.Split('&').Contains("rate=-1"))
                    {
                        entered.TrySetResult(true);
                        if (changeAccount) await release.Task;
                        else await Task.Delay(Timeout.Infinite, token);
                    }
                    return Json("{'error':0,'data':{'multirates':[{'rate':0,'name':'原画'}], 'url':'https://cdn.example/stream.flv', 'rate':0}}");
                })))
                {
                    var pending = new DouyuPlaybackClient(client).GetQualitiesAsync("123", cancel.Token);
                    await entered.Task;
                    if (changeAccount) { DouyuAccountState.SetCookie("dy_auth=another-account"); release.SetResult(true); }
                    else cancel.Cancel();
                    try { await pending; throw new InvalidOperationException("Canceled/stale menu was accepted."); }
                    catch (OperationCanceledException) { }
                }
            }
        }

        private static async Task CheckAccountChangeDuringRequestAsync()
        {
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            using (var client = new HttpClient(new Handler(async (request, token) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return Json(new JObject { ["error"] = 0, ["data"] = Descriptor() }.ToString());
            })))
            {
                var pending = new DouyuPlaybackClient(client).GetQualitiesAsync("123", CancellationToken.None);
                await entered.Task;
                DouyuAccountState.SetCookie("dy_auth=new-account");
                release.SetResult(true);
                try { await pending; throw new InvalidOperationException("Stale account result accepted."); }
                catch (OperationCanceledException) { }
            }
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
            internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) { this.send = send; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        }
        private static HttpResponseMessage Json(string text) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException("Douyu: " + message); }
        private static void Throws(Action action, string message)
        {
            try { action(); } catch (Exception) { return; }
            throw new InvalidOperationException(message);
        }
        private static async Task ThrowsAsync(Func<Task> action, string message)
        {
            try { await action(); } catch (Exception) { return; }
            throw new InvalidOperationException(message);
        }
    }
}
