using Jint;
using AllLive.SignService;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;

var managed = args.Contains("--managed", StringComparer.Ordinal);
using var owner = managed ? ManagedServiceOwner.TryCreate() : null;
if (managed && owner == null) return;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    options.Limits.MaxConcurrentConnections = 32;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
    if (managed) options.Listen(IPAddress.Loopback, 0);
    else
    {
        options.Listen(IPAddress.Loopback, 8788);
        options.Listen(IPAddress.Loopback, 8789);
    }
});
await using var app = builder.Build();
using var shutdown = owner?.Token.Register(() => app.Lifetime.StopApplication());
using var stopDeadline = app.Lifetime.ApplicationStopping.Register(() => owner?.RequestStop());
const string DouyinVersionCode = "180800";
const string DouyinSdkVersion = "1.0.14-beta.0";
var douyinWebmssdkScript = new Lazy<string>(LoadDouyinWebmssdkScript);
var streams = new BilibiliStreamRegistry();
using var bilibiliProxyHttpClient = CreateBilibiliProxyHttpClient();
using var signingSlots = new SemaphoreSlim(2, 2);
using var proxySlots = new SemaphoreSlim(8, 8);
app.Use(async (context, next) =>
{
    if (managed && context.Request.Path.StartsWithSegments("/api/bilibili/live"))
    {
        // The random stream ID authorizes native players that cannot add headers.
        await next(context);
        return;
    }
    if (managed && context.Request.Headers["X-AllLive-Session"] != owner!.SessionId)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    var signing = context.Request.Path.StartsWithSegments("/api/douyu")
        || context.Request.Path.StartsWithSegments("/api/douyin");
    if (!signing) { await next(context); return; }
    if (!await signingSlots.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    try { await next(context); }
    finally { signingSlots.Release(); }
});
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1 }));

app.MapPost("/api/window/{operation}", (string operation, WindowControlRequest payload) =>
{
    // Never expose desktop control through the legacy standalone signing server.
    if (owner == null || owner.Token.IsCancellationRequested
        || (operation != "state" && operation != "toggle-maximize")) return Results.NotFound();
    if (!DesktopWindowControl.TryExecute(payload.Handle, owner.ParentProcessId,
        operation == "toggle-maximize", out var maximized))
        return Results.Conflict(new { message = "Window is unavailable." });
    return Results.Ok(new { maximized });
});

app.MapPost("/api/bilibili/stream", async (HttpRequest request) =>
{
    if (!IsBilibiliProxyRequest(request, managed)) return Results.NotFound();
    BilibiliStreamRequest? payload;
    try { payload = await request.ReadFromJsonAsync<BilibiliStreamRequest>(request.HttpContext.RequestAborted); }
    catch { return Results.Json(new { code = -1, msg = "invalid json" }); }
    var upstreamUrl = payload?.url ?? string.Empty;
    if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri)
        || (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
        return Results.Json(new { code = -1, msg = "invalid url" });
    var state = new BilibiliStreamState(upstreamUrl,
        string.IsNullOrWhiteSpace(payload?.referer) ? "https://live.bilibili.com/" : payload.referer!,
        string.IsNullOrWhiteSpace(payload?.userAgent) ? GetDefaultUserAgent() : payload.userAgent!,
        payload?.cookie ?? string.Empty);
    var id = streams.Register(state);
    if (id == null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    var port = managed ? request.HttpContext.Connection.LocalPort : 8789;
    ServiceDiagnostics.Write(() => "[BilibiliProxy] Stream registered");
    return Results.Json(new { code = 0, data = new { url = $"http://127.0.0.1:{port}/api/bilibili/live/{id}.flv" }, msg = "" });
});

app.MapGet("/api/bilibili/live/{id}.flv", async (HttpContext context, string id) =>
{
    if (!IsBilibiliProxyRequest(context.Request, managed))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var lease = streams.Acquire(id);
    if (lease == null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    if (!await proxySlots.WaitAsync(0, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    var state = lease.State;
    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    try
    {
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Get, state.Url);
        upstreamRequest.Headers.TryAddWithoutValidation("User-Agent", state.UserAgent);
        upstreamRequest.Headers.TryAddWithoutValidation("Referer", state.Referer);
        upstreamRequest.Headers.TryAddWithoutValidation("Accept", "video/x-flv,application/octet-stream,*/*");
        if (!string.IsNullOrWhiteSpace(state.Cookie)) upstreamRequest.Headers.TryAddWithoutValidation("Cookie", state.Cookie);
        upstreamRequest.Headers.Range = new RangeHeaderValue(0, null);
        cancelled.CancelAfter(TimeSpan.FromSeconds(15));
        using var upstreamResponse = await bilibiliProxyHttpClient.SendAsync(upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead, cancelled.Token);
        cancelled.CancelAfter(Timeout.InfiniteTimeSpan);
        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            ServiceDiagnostics.Write(() => $"[BilibiliProxy] Upstream status={(int)upstreamResponse.StatusCode}");
            return;
        }
        context.Response.ContentType = "video/x-flv";
        CopyLongHeader(upstreamResponse.Content.Headers.ContentLength, value => context.Response.ContentLength = value);
        CopyStringHeader(upstreamResponse.Content.Headers.ContentRange?.ToString(), value => context.Response.Headers.ContentRange = value);
        CopyStringHeader(upstreamResponse.Content.Headers.LastModified?.ToString("R"), value => context.Response.Headers.LastModified = value);
        CopyListHeader(upstreamResponse.Headers.AcceptRanges, value => context.Response.Headers.AcceptRanges = value);
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancelled.Token);
        await StreamTransfer.CopyAsync(stream, context.Response.Body, cancelled.Token);
    }
    catch (OperationCanceledException) { context.Abort(); }
    catch (Exception ex)
    {
        ServiceDiagnostics.Write(() => "[BilibiliProxy] Failed: " + ex.GetType().Name);
        if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        else context.Abort();
    }
    finally { proxySlots.Release(); }
});

app.MapPost("/api/douyu/sign", async (HttpRequest request) =>
{
    SignRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<SignRequest>();
    }
    catch
    {
        return Results.Json(new { code = -1, msg = "invalid json" });
    }

    var html = payload?.html ?? string.Empty;
    var rid = payload?.rid ?? string.Empty;
    if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(rid))
    {
        return Results.Json(new { code = -1, msg = "html or rid empty" });
    }

    try
    {
        var js = ExtractSignJs(html);
        if (string.IsNullOrEmpty(js))
        {
            js = html;
        }
        if (string.IsNullOrEmpty(js) || !js.Contains("ub98484234"))
        {
            return Results.Json(new { code = -1, msg = "sign js empty" });
        }
        js = Regex.Replace(js, @"eval.*?;}", "strc;}");

        var did = "10000000000000000000000000001501";
        var t10 = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, app.Lifetime.ApplicationStopping);
        var engine = ScriptExecution.CreateJint(budget.Token);
        engine.Execute(js);
        var jsEvalResult = engine.Evaluate("ub98484234()").ToString();
        if (string.IsNullOrEmpty(jsEvalResult))
        {
            return Results.Json(new { code = -1, msg = "ub98484234 empty" });
        }

        var vMatch = Regex.Match(jsEvalResult, @"v=(\d+)");
        var v = vMatch.Success ? vMatch.Groups[1].Value : "";
        if (string.IsNullOrEmpty(v))
        {
            return Results.Json(new { code = -1, msg = "v empty" });
        }

        var rb = Md5Hex(rid + did + t10 + v);
        var jsSign = Regex.Replace(jsEvalResult, @"return rt;}\);?", "return rt;}");
        jsSign = jsSign.Replace("(function (", "function sign(");
        jsSign = jsSign.Replace("CryptoJS.MD5(cb).toString()", $"\"{rb}\"");

        engine.Execute(jsSign);
        var signExpr = BuildSignCall(rid, did, t10);
        var args = engine.Evaluate(signExpr).ToString();
        if (string.IsNullOrEmpty(args))
        {
            return Results.Json(new { code = -1, msg = "sign empty" });
        }

        return Results.Json(new { code = 0, data = args, msg = "" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { code = -1, msg = ex.Message });
    }
});

app.MapPost("/api/douyin/sign", async (HttpRequest request) =>
{
    DouyinSignRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<DouyinSignRequest>();
    }
    catch
    {
        return Results.Json(new { code = -1, msg = "invalid json" });
    }

    var roomId = payload?.roomId ?? string.Empty;
    var uniqueId = payload?.uniqueId ?? string.Empty;
    if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(uniqueId))
    {
        return Results.Json(new { code = -1, msg = "roomId or uniqueId empty" });
    }

    try
    {
        var script = douyinWebmssdkScript.Value;
        if (string.IsNullOrWhiteSpace(script))
        {
            return Results.Json(new { code = -1, msg = "webmssdk script empty" });
        }

        var sign = BuildDouyinSignature(script, roomId, uniqueId, request.HttpContext.RequestAborted, out var signError);
        if (string.IsNullOrWhiteSpace(sign))
        {
            return Results.Json(new { code = -1, msg = string.IsNullOrWhiteSpace(signError) ? "signature empty" : signError });
        }
        return Results.Json(new { code = 0, data = new { signature = sign }, msg = "" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { code = -1, msg = ex.Message });
    }
});

app.MapPost("/api/douyin/abogus", async (HttpRequest request) =>
{
    AbogusRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<AbogusRequest>();
    }
    catch
    {
        return Results.Json(new { code = -1, msg = "invalid json" });
    }

    var url = payload?.url ?? string.Empty;
    if (string.IsNullOrWhiteSpace(url))
    {
        return Results.Json(new { code = -1, msg = "url empty" });
    }

    try
    {
        var signedUrl = DouyinAbogus.BuildSignedUrl(url, payload?.userAgent ?? "", payload?.body ?? "");
        return Results.Json(new { code = 0, data = new { url = signedUrl }, msg = "" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { code = -1, msg = ex.Message });
    }
});

await app.StartAsync(owner?.Token ?? CancellationToken.None);
if (owner != null)
{
    var address = app.Urls.Single();
    owner.PublishReady(new Uri(address).Port);
}
await app.WaitForShutdownAsync();

static string BuildDouyinSignature(string script, string roomId, string uniqueId, CancellationToken token, out string? error)
{
    error = null;
    var signParam = BuildDouyinSignParam(roomId, uniqueId);
    if (string.IsNullOrWhiteSpace(signParam))
    {
        error = "sign param empty";
        return "";
    }
    var md5 = Md5Hex(signParam);
    var quickJsResult = TryGetSignByQuickJs(script, md5, token, out var quickJsError);
    if (!string.IsNullOrWhiteSpace(quickJsResult))
    {
        return quickJsResult;
    }
    error = string.IsNullOrWhiteSpace(quickJsError) ? "quickjs sign empty" : quickJsError;
    return "";
}

static string? TryGetSignByQuickJs(string script, string md5Param, CancellationToken token, out string? error)
{
    try
    {
        var result = ScriptExecution.SignDouyin(script, md5Param, token);
        error = string.IsNullOrWhiteSpace(result) ? "signature empty" : null;
        return result;
    }
    catch (Exception ex)
    {
        ServiceDiagnostics.Write(() => "[DouyinSign] Failed: " + ex.GetType().Name);
        error = "signature execution failed";
        return null;
    }
}

static string BuildDouyinSignParam(string roomId, string uniqueId)
{
    var pairs = new List<KeyValuePair<string, string>>
    {
        new KeyValuePair<string, string>("live_id", "1"),
        new KeyValuePair<string, string>("aid", "6383"),
        new KeyValuePair<string, string>("version_code", DouyinVersionCode),
        new KeyValuePair<string, string>("webcast_sdk_version", DouyinSdkVersion),
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

static string LoadDouyinWebmssdkScript()
{
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "douyin-webmssdk.js");
        if (!File.Exists(path))
        {
            return "";
        }
        return File.ReadAllText(path, Encoding.UTF8);
    }
    catch
    {
        return "";
    }
}

static string ExtractSignJs(string html)
{
    var match = Regex.Match(html, @"(vdwdae325w_64we[\s\S]*function ub98484234[\s\S]*?)function", RegexOptions.Singleline);
    return match.Groups.Count > 1 ? match.Groups[1].Value : "";
}

static string Md5Hex(string input)
{
    using var md5 = MD5.Create();
    var bytes = Encoding.UTF8.GetBytes(input);
    var hash = md5.ComputeHash(bytes);
    var sb = new StringBuilder(hash.Length * 2);
    foreach (var b in hash)
    {
        sb.Append(b.ToString("x2"));
    }
    return sb.ToString();
}

static string BuildSignCall(string rid, string did, string t10)
{
    if (long.TryParse(rid, out var ridNum))
    {
        return $"sign({ridNum},'{EscapeJs(did)}',{t10})";
    }
    return $"sign('{EscapeJs(rid)}','{EscapeJs(did)}',{t10})";
}

static string EscapeJs(string value)
{
    return value.Replace("\\", "\\\\").Replace("'", "\\'");
}

static HttpClient CreateBilibiliProxyHttpClient()
{
    var handler = new HttpClientHandler()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseProxy = false,
    };

    return new HttpClient(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}

static bool IsBilibiliProxyRequest(HttpRequest request, bool managed)
{
    if (!managed && request.HttpContext.Connection.LocalPort != 8789)
    {
        return false;
    }

    var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
    return remoteIp == null || IPAddress.IsLoopback(remoteIp);
}

static void CopyLongHeader(long? value, Action<long> assign)
{
    if (value.HasValue)
    {
        assign(value.Value);
    }
}

static void CopyStringHeader(string? value, Action<string> assign)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        assign(value);
    }
}

static void CopyListHeader(IEnumerable<string> values, Action<string> assign)
{
    var value = values == null ? string.Empty : string.Join(",", values.Where(x => !string.IsNullOrWhiteSpace(x)));
    if (!string.IsNullOrWhiteSpace(value))
    {
        assign(value);
    }
}

static string GetDefaultUserAgent()
{
    return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
}

record BilibiliStreamRequest(string url, string? referer, string? userAgent, string? cookie);
record BilibiliStreamState(string Url, string Referer, string UserAgent, string Cookie);
record DouyinSignRequest(string roomId, string uniqueId);
record SignRequest(string html, string rid);
record AbogusRequest(string url, string? userAgent, string? body);
