using AllLive.Core.Helper;
using AllLive.Core.Interface;
using AllLive.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;
/*
* 斗鱼弹幕实现
* 参考项目：
* https://github.com/IsoaSFlus/danmaku
* https://www.cnblogs.com/sdflysha/p/20210117-douyu-barrage-with-dotnet.html
* 
* 斗鱼如果使用System.Net.WebSockets.ClientWebSocket,30秒后会被关闭连接...
*/

namespace AllLive.Core.Danmaku
{
    public class DouyuDanmaku : ILiveDanmaku
    {
        public int HeartbeatTime => 45 * 1000;

        public event EventHandler<LiveMessage> NewMessage;
        public event EventHandler<string> OnClose;
        private readonly string ServerUrl = "wss://danmuproxy.douyu.com:8506";
        Timer timer;
        WebSocket ws;
        string roomId;
        bool isStopped = true;
        public DouyuDanmaku()
        {
        }
        private async void Ws_OnOpen(object sender, EventArgs e)
        {
            var socket = ws;
            if (isStopped || socket == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                if (isStopped)
                {
                    return;
                }
                //发送进房信息
                socket.Send(SerializeDouyu($"type@=loginreq/roomid@={roomId}/"));
                socket.Send(SerializeDouyu($"type@=joingroup/rid@={roomId}/gid@=-9999/"));
            });
            if (!isStopped)
            {
                timer?.Start();
            }

        }
        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            try
            {
                string result = DeserializeDouyu(e.RawData);
                if (result.Length != 0)
                {
                    var json = SttToJObject(result);
                    var type = json["type"]?.ToString();
                    //斗鱼好像不会返回人气值
                    //有些直播间存在阴间弹幕，不知道什么情况
                    if (type == "chatmsg")
                    {
                        // 屏蔽机器人弹幕
                        if (json["dms"] == null)
                        {
                            return;
                        }

                        NewMessage?.Invoke(this, new LiveMessage()
                        {
                            UserName = json["nn"].ToString(),
                            Message = json["txt"].ToString(),
                            Color= GetColor(json["col"].ToInt32())
                        });
                    }

                }

            }
            catch (Exception)
            {
            }
        }

        private DanmakuColor GetColor(int type)
        {
            switch (type)
            {
                case 1:
                    return DanmakuColor.Red;
                case 2:
                    return DanmakuColor.FromRgb(30, 135, 240);
                case 3:
                    return DanmakuColor.FromRgb(122, 200, 75);
                case 4:
                    return DanmakuColor.FromRgb(255, 127, 0);
                case 5:
                    return DanmakuColor.FromRgb(155, 57, 244);
                case 6:
                    return DanmakuColor.FromRgb(255, 105, 180);
                default:
                    return DanmakuColor.White;
            }
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            OnClose?.Invoke(this, e.Reason);
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            OnClose?.Invoke(this, e.Message);
        }



        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (isStopped)
            {
                return;
            }

            Heartbeat();
        }

        public async void Heartbeat()
        {
            var socket = ws;
            if (isStopped || socket == null)
            {
                return;
            }

            await Task.Run(() =>
            {
                if (isStopped)
                {
                    return;
                }
                socket.Send(SerializeDouyu($"type@=mrkl/"));
            });
        }

        public async Task Start(object args)
        {
            isStopped = false;
            this.roomId = args.ToString();
            ws = new WebSocket(ServerUrl);
            ws.OnOpen += Ws_OnOpen;
            ws.OnError += Ws_OnError;
            ws.OnMessage += Ws_OnMessage;
            ws.OnClose += Ws_OnClose;
            timer = new Timer(HeartbeatTime);
            timer.Elapsed += Timer_Elapsed;
            await Task.Run(() =>
            {
                if (!isStopped)
                {
                    ws?.Connect();
                }
            });
        }

        public async Task Stop()
        {
            isStopped = true;
            StopTimer();
            await Task.Run(() =>
            {
                var socket = ws;
                ws = null;
                if (socket == null)
                {
                    return;
                }
                DetachWebSocketEvents(socket);
                try { socket.Close(); } catch { }
            });
        }

        private void StopTimer()
        {
            var currentTimer = timer;
            timer = null;
            if (currentTimer == null)
            {
                return;
            }
            currentTimer.Stop();
            currentTimer.Elapsed -= Timer_Elapsed;
            currentTimer.Dispose();
        }

        private void DetachWebSocketEvents(WebSocket socket)
        {
            if (socket == null)
            {
                return;
            }
            socket.OnOpen -= Ws_OnOpen;
            socket.OnError -= Ws_OnError;
            socket.OnMessage -= Ws_OnMessage;
            socket.OnClose -= Ws_OnClose;
        }

        private byte[] SerializeDouyu(string body)
        {
            const short ClientSendToServer = 689;
            const byte Encrypted = 0;
            const byte Reserved = 0;

            byte[] bodyBuffer = Encoding.UTF8.GetBytes(body);
            using (var ms = new MemoryStream(bodyBuffer.Length + 13))
            {
                using (var writer = new BinaryWriter(ms))
                {
                    writer.Write(4 + 4 + body.Length + 1);
                    writer.Write(4 + 4 + body.Length + 1);
                    writer.Write(ClientSendToServer);
                    writer.Write(Encrypted);
                    writer.Write(Reserved);
                    writer.Write(bodyBuffer);
                    writer.Write((byte)0);
                    writer.Flush();

                    return ms.ToArray();
                }
            }



        }

        private string DeserializeDouyu(byte[] bytes)
        {

            try
            {
                using (var ms = new MemoryStream(bytes, 0, bytes.Length, writable: false))

                using (var reader = new BinaryReader(ms))
                {
                    int fullMsgLength = reader.ReadInt32();
                    int fullMsgLength2 = reader.ReadInt32();

                    int bodyLength = fullMsgLength - 1 - 4 - 4;
                    short packType = reader.ReadInt16();
                    short encrypted = reader.ReadByte();
                    short reserved = reader.ReadByte();
                    var _bytes = reader.ReadBytes(bodyLength);
                    byte zero = reader.ReadByte();
                    return Encoding.UTF8.GetString(_bytes);
                }

            }
            catch (Exception)
            {

                return "";
            }

        }
        //辣鸡STT
        private JToken SttToJObject(string str)
        {
            if (str.Contains("//"))
            {
                var result = new JArray();
                foreach (var field in str.Split(new[] { "//" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    result.Add(SttToJObject(field));
                }
                return result;
            }
            if (str.Contains("@="))
            {
                var result = new JObject();
                foreach (var field in str.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var tokens = field.Split(new[] { "@=" }, StringSplitOptions.None);
                    var k = tokens[0];
                    var v = UnscapeSlashAt(tokens[1]);
                    result[k] = SttToJObject(v);
                }
                return result;
            }
            else if (str.Contains("@A="))
            {
                return SttToJObject(UnscapeSlashAt(str));
            }
            else
            {
                return UnscapeSlashAt(str);
            }


        }
        private string UnscapeSlashAt(string str)
        {
            return str
                .Replace("@S", "/")
                .Replace("@A", "@");
        }

    }
}
