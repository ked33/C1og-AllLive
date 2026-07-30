using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace AllLive.Core.Danmaku
{
    public sealed class TwitchDanmakuArgs
    {
        public string Channel { get; set; }
        public Func<Task<long?>> ViewerCountProvider { get; set; }
    }

    /// <summary>
    /// Twitch 匿名 IRC 聊天实现。
    /// </summary>
    public sealed class TwitchDanmaku : ILiveDanmaku
    {
        private const string ServerUrl = "wss://irc-ws.chat.twitch.tv:443";
        private const int ViewerCountRefreshInterval = 60 * 1000;

        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket ws;
        private CancellationTokenSource connectionCts;
        private Task receiveTask;
        private TwitchDanmakuArgs args;
        private System.Timers.Timer viewerCountTimer;
        private volatile bool isStopped = true;
        private int closeRaised;
        private int viewerCountRefreshInProgress;

        public int HeartbeatTime => 60 * 1000;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;

        public void Heartbeat()
        {
            _ = SendRawSafelyAsync("PING :alllive");
        }

        public async Task Start(object args)
        {
            var twitchArgs = args as TwitchDanmakuArgs;
            if (twitchArgs == null
                || string.IsNullOrWhiteSpace(twitchArgs.Channel)
                || !Regex.IsMatch(twitchArgs.Channel, @"^[0-9A-Za-z_]+$"))
            {
                // 未开播房间仍会经过通用弹幕启动流程。没有有效参数时直接
                // 保持 no-op，不让弹幕影响房间详情或播放页面。
                isStopped = true;
                return;
            }

            this.args = twitchArgs;
            this.args.Channel = this.args.Channel.Trim().ToLowerInvariant();
            isStopped = false;
            Interlocked.Exchange(ref closeRaised, 0);
            Interlocked.Exchange(ref viewerCountRefreshInProgress, 0);

            var cts = new CancellationTokenSource();
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            connectionCts = cts;
            ws = socket;

            try
            {
                await socket.ConnectAsync(new Uri(ServerUrl), cts.Token);
                if (isStopped || !ReferenceEquals(ws, socket))
                {
                    return;
                }

                var nick = "justinfan" + (DateTime.UtcNow.Ticks % 1000000000L).ToString(CultureInfo.InvariantCulture);
                await SendRawAsync("CAP REQ :twitch.tv/membership twitch.tv/tags twitch.tv/commands", socket, cts.Token);
                await SendRawAsync("PASS SCHMOOPIIE", socket, cts.Token);
                await SendRawAsync("NICK " + nick, socket, cts.Token);
                await SendRawAsync($"USER {nick} 8 * :{nick}", socket, cts.Token);
                await SendRawAsync("JOIN #" + this.args.Channel, socket, cts.Token);

                StartViewerCountTimer();
                receiveTask = ReceiveLoopAsync(socket, cts.Token);
            }
            catch
            {
                isStopped = true;
                StopViewerCountTimer();
                if (ReferenceEquals(ws, socket))
                {
                    ws = null;
                }
                if (ReferenceEquals(connectionCts, cts))
                {
                    connectionCts = null;
                }
                try { cts.Cancel(); } catch { }
                try { socket.Abort(); } catch { }
                socket.Dispose();
                cts.Dispose();
                throw;
            }
        }

        public async Task Stop()
        {
            isStopped = true;
            Interlocked.Exchange(ref closeRaised, 1);
            Interlocked.Exchange(ref viewerCountRefreshInProgress, 0);
            StopViewerCountTimer();
            args = null;

            var socket = ws;
            var cts = connectionCts;
            var activeReceiveTask = receiveTask;
            ws = null;
            connectionCts = null;
            receiveTask = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }

            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    {
                        using (var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "AllLive stopped", closeCts.Token);
                        }
                    }
                }
                catch
                {
                    try { socket.Abort(); } catch { }
                }
            }

            if (activeReceiveTask != null)
            {
                try { await activeReceiveTask; } catch { }
            }

            socket?.Dispose();
            cts?.Dispose();
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            string closeReason = null;
            var buffer = new byte[16 * 1024];
            using (var messageStream = new MemoryStream())
            {
                try
                {
                    while (!isStopped && !token.IsCancellationRequested
                        && (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived))
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            closeReason = socket.CloseStatusDescription;
                            break;
                        }

                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            messageStream.Write(buffer, 0, result.Count);
                        }
                        if (!result.EndOfMessage)
                        {
                            continue;
                        }

                        if (result.MessageType == WebSocketMessageType.Text && messageStream.Length > 0)
                        {
                            var text = Encoding.UTF8.GetString(messageStream.ToArray());
                            closeReason = await ProcessIrcMessageAsync(text, socket, token);
                        }
                        messageStream.SetLength(0);
                        if (!string.IsNullOrWhiteSpace(closeReason))
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!isStopped)
                    {
                        closeReason = "Twitch IRC连接已取消";
                    }
                }
                catch (Exception ex)
                {
                    closeReason = ex.Message;
                }
            }

            if (!isStopped)
            {
                RaiseClose(closeReason);
            }
        }

        private async Task<string> ProcessIrcMessageAsync(string data, ClientWebSocket socket, CancellationToken token)
        {
            var lines = (data ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine?.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (line.StartsWith("PING ", StringComparison.OrdinalIgnoreCase))
                {
                    await SendRawAsync("PONG " + line.Substring(5), socket, token);
                    continue;
                }
                if (line.IndexOf(" RECONNECT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Twitch要求重新连接";
                }

                LiveMessage message;
                if (TryParsePrivMsg(line, out message))
                {
                    NewMessage?.Invoke(this, message);
                }
            }
            return null;
        }

        private async Task SendRawSafelyAsync(string value)
        {
            try
            {
                var socket = ws;
                var cts = connectionCts;
                if (socket == null || cts == null)
                {
                    return;
                }
                await SendRawAsync(value, socket, cts.Token);
            }
            catch (Exception ex)
            {
                RaiseClose(ex.Message);
            }
        }

        private async Task SendRawAsync(string value, ClientWebSocket socket, CancellationToken token)
        {
            if (isStopped || socket == null || socket.State != WebSocketState.Open
                || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value + "\r\n");
            await sendLock.WaitAsync(token);
            try
            {
                if (!isStopped && socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        token);
                }
            }
            finally
            {
                sendLock.Release();
            }
        }

        private void RaiseClose(string reason)
        {
            if (isStopped || Interlocked.CompareExchange(ref closeRaised, 1, 0) != 0)
            {
                return;
            }
            StopViewerCountTimer();
            OnClose?.Invoke(this, string.IsNullOrWhiteSpace(reason) ? "Twitch IRC连接已关闭" : reason);
        }

        private void StartViewerCountTimer()
        {
            StopViewerCountTimer();
            if (args?.ViewerCountProvider == null)
            {
                return;
            }
            var timer = new System.Timers.Timer(ViewerCountRefreshInterval)
            {
                AutoReset = true,
            };
            timer.Elapsed += ViewerCountTimer_Elapsed;
            viewerCountTimer = timer;
            timer.Start();
        }

        private async void ViewerCountTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var provider = args?.ViewerCountProvider;
            if (isStopped || provider == null
                || Interlocked.CompareExchange(ref viewerCountRefreshInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var viewerCount = await provider();
                if (!isStopped && viewerCount.HasValue)
                {
                    NewMessage?.Invoke(this, new LiveMessage()
                    {
                        Type = LiveMessageType.Online,
                        Data = viewerCount.Value,
                        AudienceMetricKind = LiveAudienceMetricKind.ViewerCount,
                    });
                }
            }
            catch
            {
                // 在线人数刷新失败不应中断仍然正常工作的 IRC 聊天连接。
            }
            finally
            {
                Interlocked.Exchange(ref viewerCountRefreshInProgress, 0);
            }
        }

        private void StopViewerCountTimer()
        {
            var timer = viewerCountTimer;
            viewerCountTimer = null;
            if (timer == null)
            {
                return;
            }
            timer.Stop();
            timer.Elapsed -= ViewerCountTimer_Elapsed;
            timer.Dispose();
        }

        private static bool TryParsePrivMsg(string line, out LiveMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var remainder = line;
            if (line[0] == '@')
            {
                var tagsEnd = line.IndexOf(' ');
                if (tagsEnd <= 1)
                {
                    return false;
                }
                ParseTags(line.Substring(1, tagsEnd - 1), tags);
                remainder = line.Substring(tagsEnd + 1);
            }

            var commandIndex = remainder.IndexOf(" PRIVMSG ", StringComparison.OrdinalIgnoreCase);
            if (commandIndex < 0)
            {
                return false;
            }
            var textIndex = remainder.IndexOf(" :", commandIndex + 9, StringComparison.Ordinal);
            if (textIndex < 0 || textIndex + 2 > remainder.Length)
            {
                return false;
            }

            var text = remainder.Substring(textIndex + 2);
            if (text.StartsWith("\u0001ACTION ", StringComparison.Ordinal)
                && text.EndsWith("\u0001", StringComparison.Ordinal)
                && text.Length > 9)
            {
                text = text.Substring(8, text.Length - 9);
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string userName;
            if (!tags.TryGetValue("display-name", out userName) || string.IsNullOrWhiteSpace(userName))
            {
                userName = ReadPrefixUserName(remainder);
            }
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = "Twitch用户";
            }

            message = new LiveMessage()
            {
                Type = LiveMessageType.Chat,
                UserName = userName,
                Message = text,
                Color = ParseColor(tags),
            };
            return true;
        }

        private static void ParseTags(string value, IDictionary<string, string> target)
        {
            foreach (var item in (value ?? string.Empty).Split(';'))
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }
                var separator = item.IndexOf('=');
                var key = separator < 0 ? item : item.Substring(0, separator);
                var tagValue = separator < 0 || separator + 1 >= item.Length
                    ? string.Empty
                    : UnescapeTagValue(item.Substring(separator + 1));
                if (!string.IsNullOrWhiteSpace(key))
                {
                    target[key] = tagValue;
                }
            }
        }

        private static string UnescapeTagValue(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            {
                return value;
            }
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(value[i]);
                    continue;
                }
                switch (value[++i])
                {
                    case 's':
                        builder.Append(' ');
                        break;
                    case ':':
                        builder.Append(';');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    default:
                        builder.Append(value[i]);
                        break;
                }
            }
            return builder.ToString();
        }

        private static string ReadPrefixUserName(string remainder)
        {
            if (string.IsNullOrWhiteSpace(remainder) || remainder[0] != ':')
            {
                return null;
            }
            var end = remainder.IndexOf('!');
            return end > 1 ? remainder.Substring(1, end - 1) : null;
        }

        private static DanmakuColor ParseColor(IDictionary<string, string> tags)
        {
            string color;
            if (tags != null && tags.TryGetValue("color", out color)
                && Regex.IsMatch(color ?? string.Empty, @"^#[0-9A-Fa-f]{6}$"))
            {
                try
                {
                    return new DanmakuColor(color);
                }
                catch
                {
                }
            }
            return DanmakuColor.White;
        }
    }
}
