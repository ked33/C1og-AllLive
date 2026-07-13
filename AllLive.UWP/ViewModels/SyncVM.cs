using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Microsoft.AspNetCore.SignalR.Client;
using AllLive.UWP.Helper;
using System.Data.Common;
using System.Data;
using Microsoft.Toolkit.Uwp.Helpers;
using Newtonsoft.Json;
using AllLive.Core.Danmaku.Proto;
using Windows.UI.Xaml;
using Windows.UI.Core;
using System.Timers;
using Windows.UI.Popups;
using AllLive.UWP.Models;
using Windows.System;
using Newtonsoft.Json.Linq;

namespace AllLive.UWP.ViewModels
{
    public class SyncVM : BaseViewModel
    {
        const string URL = "https://sync1.nsapps.cn/sync";
        // 同步服务当前使用 SignalR 默认 32 KiB 单消息上限；按完整外层 JSON 帧预估并预留 4 KiB 余量。
        private const int MaxSyncInvocationBytes = 28 * 1024;
        private const string InvocationIdSizeProbe = "99999999999999999999";
        public SyncVM() { }

        private bool _RoomConnected = false;
        public bool RoomConnected
        {
            get { return _RoomConnected; }
            set { _RoomConnected = value; DoPropertyChanged("RoomConnected"); }
        }

        private string _RoomID = "--";
        public string RoomID
        {
            get { return _RoomID; }
            set { _RoomID = value; DoPropertyChanged("RoomID"); }
        }

        private bool _IsCreator = false;
        public bool IsCreator
        {
            get { return _IsCreator; }
            set { _IsCreator = value; DoPropertyChanged("IsCreator"); }
        }

        public ObservableCollection<RoomUser> RoomUsers { get; set; } = new ObservableCollection<RoomUser>();

        private bool _SignalRConnecting = false;
        public bool SignalRConnecting
        {
            get { return _SignalRConnecting; }
            set { _SignalRConnecting = value; DoPropertyChanged("SignalRConnecting"); }
        }

        private int _Countdown = 600;
        public int Countdown
        {
            get { return _Countdown; }
            set { _Countdown = value; DoPropertyChanged("Countdown"); }
        }

        HubConnection connection;
        private Func<Exception, Task> connectionClosedHandler;
        private bool isDisconnecting;
        private bool isSendingFollow;
        public async void ConnectSignalR(string roomId)
        {
            try
            {
                if (SignalRConnecting)
                {
                    Utils.ShowMessageToast("正在连接中");
                    return;
                }
                SignalRConnecting = true;
                await DisconnectSignalRAsync(false);
                connection = new HubConnectionBuilder()
                   .WithUrl(URL)
                   .Build();
                connectionClosedHandler = async (error) =>
                {
                    if (!isDisconnecting)
                    {
                        RoomConnected = false;
                        Utils.ShowMessageToast("连接已断开");
                        LogHelper.Log("连接已断开", LogType.ERROR, error);
                    }
                    await Task.CompletedTask;
                };
                connection.Closed += connectionClosedHandler;
                await connection.StartAsync();
                ListenSignalR();
                if (roomId == null || roomId == "")
                {
                    IsCreator = true;
                    await CreateRoom();
                }
                else
                {
                    IsCreator = false;
                    await JoinRoom(roomId);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log("连接失败", LogType.ERROR, ex);
                Utils.ShowMessageToast($"连接失败：{ex.Message}");
                await DisconnectSignalRAsync();
            }
            finally
            {
                SignalRConnecting = false;
            }
        }

        public void ListenSignalR()
        {
            var activeConnection = connection;
            if (activeConnection == null)
            {
                return;
            }

            activeConnection.On<bool, string>("onFavoriteReceived", (overlay, content) =>
            {
                if (!IsCurrentConnection(activeConnection))
                {
                    return;
                }
                ReceiveFavorite(overlay, content);
            });
            activeConnection.On<bool, string>("onHistoryReceived", (overlay, content) =>
            {
                if (!IsCurrentConnection(activeConnection))
                {
                    return;
                }
                ReceiveHistory(overlay, content);
            });
            activeConnection.On<bool, string>("onShieldWordReceived", (overlay, content) =>
            {
                if (!IsCurrentConnection(activeConnection))
                {
                    return;
                }
                ReceiveShieldWord(overlay, content);
            });
            activeConnection.On<bool, string>("onBiliAccountReceived", (overlay, content) =>
            {
                if (!IsCurrentConnection(activeConnection))
                {
                    return;
                }
                ReceiveBiliBili(overlay, content);
            });
            activeConnection.On<string>("onRoomDestroyed", (roomName) =>
            {
                if (!IsCurrentConnection(activeConnection))
                {
                    return;
                }
                ShowMessage("房间已销毁");
                DisconnectSignalR();
            });

            activeConnection.On<List<RoomUser>>("onUserUpdated", (user) =>
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!IsCurrentConnection(activeConnection))
                    {
                        return;
                    }
                    RoomUsers.Clear();
                    foreach (var u in user)
                    {
                        if (u.ConnectionId == activeConnection.ConnectionId)
                        {
                            u.IsCurrentUser = true;
                        }
                        RoomUsers.Add(u);
                    }
                });
            });

        }

        private void ReceiveFavorite(bool overlay, string content)
        {
            try
            {
                var items = JsonConvert.DeserializeObject<List<FavoriteJsonItem>>(content);
                if (items == null)
                {
                    throw new JsonSerializationException("关注列表内容为空或格式无效");
                }

                if (overlay)
                {
                    DatabaseHelper.DeleteFavorite();
                }
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.SiteId) || string.IsNullOrWhiteSpace(item.RoomId))
                    {
                        continue;
                    }
                    if (DatabaseHelper.CheckFavorite(item.RoomId, item.SiteName) == null)
                    {
                        DatabaseHelper.AddFavorite(new FavoriteItem()
                        {
                            SiteName = item.SiteName,
                            RoomID = item.RoomId,
                            UserName = item.UserName,
                            Photo = item.Face,
                            SortOrder = item.Sort,
                        });
                    }
                }
                ShowMessage("已同步关注列表");
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                 {
                     MessageCenter.UpdateFavorite();
                 });
            }
            catch (Exception ex)
            {
                LogHelper.Log("接收关注列表失败", LogType.ERROR, ex);
                ShowMessage("接收关注列表失败，请稍后重试");
            }
        }

        private void ReceiveHistory(bool overlay, string content)
        {
            if (overlay)
            {
                DatabaseHelper.DeleteHistory();
            }
            var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<HistoryJsonItem>>(content);
            foreach (var item in items)
            {
                DatabaseHelper.AddHistory(new HistoryItem()
                {
                    WatchTime = DateTime.Parse(item.UpdateTime),
                    SiteName = item.SiteName,
                    RoomID = item.RoomId,
                    UserName = item.UserName,
                    Photo = item.Face,
                });
            }
            ShowMessage("已同步历史记录");
        }

        private void ReceiveShieldWord(bool overlay, string content)
        {
            var currentWords = JsonConvert.DeserializeObject<List<string>>(SettingHelper.GetValue<string>(SettingHelper.LiveDanmaku.SHIELD_WORD, "[]"));
            if(overlay)
            {
                currentWords.Clear();
            }
            var words = JsonConvert.DeserializeObject<List<string>>(content);
            foreach (var word in words)
            {
                if (!currentWords.Contains(word))
                {
                    currentWords.Add(word);
                }
            }
            SettingHelper.SetValue(SettingHelper.LiveDanmaku.SHIELD_WORD, JsonConvert.SerializeObject(currentWords));
            ShowMessage("已同步屏蔽词");

        }

        private  void ReceiveBiliBili(bool overlay, string content)
        {
            var obj = JObject.Parse(content);
            var cookie = obj["cookie"];
            SettingHelper.SetValue(SettingHelper.BILI_COOKIE, cookie);
            _=Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                await BiliAccount.Instance.LoadUserInfo();
            });
          
            ShowMessage("已同步哔哩哔哩账号");
        }

        public CoreDispatcher Dispatcher { get; set; }
        public void ShowMessage(string message)
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                Utils.ShowMessageToast(message);
            });
        }

        public void DisconnectSignalR()
        {
            _ = DisconnectSignalRAsync();
        }

        private async Task DisconnectSignalRAsync(bool resetState = true)
        {
            StopTimer();
            var oldConnection = connection;
            var oldConnectionClosedHandler = connectionClosedHandler;
            connection = null;
            connectionClosedHandler = null;

            if (oldConnection != null)
            {
                try
                {
                    isDisconnecting = true;
                    if (oldConnectionClosedHandler != null)
                    {
                        oldConnection.Closed -= oldConnectionClosedHandler;
                    }
                    await oldConnection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    LogHelper.Log("断开同步连接失败", LogType.ERROR, ex);
                }
                finally
                {
                    isDisconnecting = false;
                }
            }

            if (!resetState)
            {
                return;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                SignalRConnecting = false;
                RoomConnected = false;
                RoomID = "--";
                RoomUsers.Clear();
            });
        }

        public async Task CreateRoom()
        {
            var activeConnection = GetConnectedConnectionOrNotify();
            if (activeConnection == null)
            {
                return;
            }
            string app = "聚合直播";
            string platform = Utils.IsXbox ? "xbox" : "windows";
            string version = $"{SystemInformation.Instance.ApplicationVersion.Major}.{SystemInformation.Instance.ApplicationVersion.Minor}.{SystemInformation.Instance.ApplicationVersion.Build}"; ;
            var resp = await activeConnection.InvokeAsync<Resp<string>>("CreateRoom", app, platform, version);
            if (resp.IsSuccess)
            {
                RoomConnected = true;
                RoomID = resp.Data;
                StartTimer();
            }
            else
            {
                Utils.ShowMessageToast(resp.Message);
                DisconnectSignalR();
            }
        }
        Timer timer;

        private void StartTimer()
        {
            StopTimer();
            Countdown = 600;
            timer = new Timer(1000);
            timer.Elapsed += Timer_Elapsed;
            timer.Start();
        }

        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Countdown--;
            });
            if (Countdown <= 1)
            {
                DisconnectSignalR();
            }
        }

        private void StopTimer()
        {
            if (timer == null)
            {
                return;
            }
            timer.Stop();
            timer.Elapsed -= Timer_Elapsed;
            timer.Dispose();
            timer = null;
        }

        public async Task JoinRoom(string roomId)
        {
            var activeConnection = GetConnectedConnectionOrNotify();
            if (activeConnection == null)
            {
                return;
            }
            string app = "聚合直播";
            string platform = Utils.IsXbox ? "xbox" : "windows";
            string version = $"{SystemInformation.Instance.ApplicationVersion.Major}.{SystemInformation.Instance.ApplicationVersion.Minor}.{SystemInformation.Instance.ApplicationVersion.Build}"; ;
            var resp = await activeConnection.InvokeAsync<Resp<int>>("JoinRoom", roomId.ToUpper(), app, platform, version);
            if (resp.IsSuccess)
            {
                RoomConnected = true;
                RoomID = roomId.ToUpper();
            }
            else
            {
                Utils.ShowMessageToast(resp.Message);
                DisconnectSignalR();
            }
        }


        public async Task SendFollowAsync()
        {
            if (isSendingFollow)
            {
                ShowMessage("正在发送关注列表，请稍候");
                return;
            }

            isSendingFollow = true;
            var sentBatchCount = 0;
            try
            {
                var activeConnection = GetConnectedConnectionOrNotify();
                if (activeConnection == null)
                {
                    return;
                }
                if (RoomUsers.Count <= 1)
                {
                    ShowMessage("无设备连接");
                    return;
                }

                var overlay = await ShowOverlayDialog();
                if (!IsConnectionReady(activeConnection))
                {
                    await DisconnectSignalRAsync();
                    ShowMessage("连接已断开，请重新创建或加入房间");
                    return;
                }

                var followList = await DatabaseHelper.GetFavorites();
                if (followList.Count == 0)
                {
                    ShowMessage("没有关注的直播间");
                    return;
                }

                var items = new List<FavoriteJsonItem>();
                foreach (var item in followList)
                {
                    var siteId = GetSiteId(item.SiteName);
                    if (string.IsNullOrWhiteSpace(siteId))
                    {
                        LogHelper.Log($"跳过不支持同步的关注站点：{item.SiteName}", LogType.ERROR);
                        continue;
                    }

                    items.Add(new FavoriteJsonItem()
                    {
                        SiteId = siteId,
                        Id = $"{siteId}_{item.RoomID}",
                        RoomId = item.RoomID,
                        UserName = item.UserName,
                        Face = item.Photo,
                        AddTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.M"),
                        Sort = item.SortOrder,
                    });
                }

                if (items.Count == 0)
                {
                    ShowMessage("没有可同步的关注直播间");
                    return;
                }

                var batches = BuildSyncBatches("SendFavorite", RoomID, items);
                for (var index = 0; index < batches.Count; index++)
                {
                    if (!IsConnectionReady(activeConnection))
                    {
                        throw new InvalidOperationException("同步连接已断开");
                    }

                    var batch = batches[index];
                    // 覆盖模式只允许首批清空远端；后续批次必须追加，否则最终只会保留最后一批。
                    var batchOverlay = index == 0 && overlay;
                    var resp = await activeConnection.InvokeAsync<Resp<int>>("SendFavorite", RoomID, batchOverlay, batch.Content);
                    if (resp == null || !resp.IsSuccess)
                    {
                        var message = string.IsNullOrWhiteSpace(resp?.Message) ? "服务端未接受数据" : resp.Message;
                        var partialMessage = sentBatchCount > 0 ? "，远端可能已收到部分数据" : "";
                        ShowMessage($"关注列表发送中断（{index + 1}/{batches.Count}）：{message}{partialMessage}");
                        return;
                    }

                    sentBatchCount++;
                    LogHelper.Log($"发送关注列表分块 {index + 1}/{batches.Count}，条目={batch.ItemCount}，估算帧字节={batch.InvocationBytes}", LogType.INFO);
                }

                ShowMessage(batches.Count == 1 ? "发送成功" : $"发送成功，共 {batches.Count} 批");
            }
            catch (SyncPayloadTooLargeException ex)
            {
                LogHelper.Log("关注列表包含超出同步限制的单条数据", LogType.ERROR, ex);
                ShowMessage(ex.Message);
            }
            catch (Exception ex)
            {
                LogHelper.Log($"发送关注列表失败，已发送批次={sentBatchCount}", LogType.ERROR, ex);
                if (sentBatchCount > 0)
                {
                    ShowMessage("关注列表发送中断，远端可能已收到部分数据；请重新连接后再次发送");
                }
                else
                {
                    ShowMessage("发送关注列表失败，请重新连接后重试");
                }

                if (connection == null || connection.State != HubConnectionState.Connected)
                {
                    await DisconnectSignalRAsync();
                }
            }
            finally
            {
                isSendingFollow = false;
            }
        }

        private static string GetSiteId(string siteName)
        {
            switch (siteName)
            {
                case "哔哩哔哩直播":
                    return "bilibili";
                case "斗鱼直播":
                    return "douyu";
                case "虎牙直播":
                    return "huya";
                case "抖音直播":
                    return "douyin";
                default:
                    return "";
            }
        }

        private static List<SyncDataBatch> BuildSyncBatches<T>(string methodName, string roomId, IList<T> items)
        {
            var batches = new List<SyncDataBatch>();
            var currentItems = new List<T>();

            foreach (var item in items)
            {
                currentItems.Add(item);
                var candidateContent = JsonConvert.SerializeObject(currentItems);
                var candidateBytes = EstimateSyncInvocationBytes(methodName, roomId, candidateContent);
                if (candidateBytes <= MaxSyncInvocationBytes)
                {
                    continue;
                }

                currentItems.RemoveAt(currentItems.Count - 1);
                if (currentItems.Count == 0)
                {
                    throw new SyncPayloadTooLargeException("关注列表中存在单条过大的数据，无法安全发送");
                }

                AddSyncBatch(batches, methodName, roomId, currentItems);
                currentItems = new List<T>() { item };

                var singleItemContent = JsonConvert.SerializeObject(currentItems);
                if (EstimateSyncInvocationBytes(methodName, roomId, singleItemContent) > MaxSyncInvocationBytes)
                {
                    throw new SyncPayloadTooLargeException("关注列表中存在单条过大的数据，无法安全发送");
                }
            }

            if (currentItems.Count > 0)
            {
                AddSyncBatch(batches, methodName, roomId, currentItems);
            }

            return batches;
        }

        private static void AddSyncBatch<T>(ICollection<SyncDataBatch> batches, string methodName, string roomId, IList<T> items)
        {
            var content = JsonConvert.SerializeObject(items);
            batches.Add(new SyncDataBatch()
            {
                Content = content,
                ItemCount = items.Count,
                InvocationBytes = EstimateSyncInvocationBytes(methodName, roomId, content),
            });
        }

        private static int EstimateSyncInvocationBytes(string methodName, string roomId, string content)
        {
            var invocation = new
            {
                type = 1,
                invocationId = InvocationIdSizeProbe,
                target = methodName,
                arguments = new object[] { roomId, false, content },
            };
            var settings = new JsonSerializerSettings()
            {
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
            };
            var wireJson = JsonConvert.SerializeObject(invocation, Formatting.None, settings);
            return Encoding.UTF8.GetByteCount(wireJson) + 1;
        }

        private bool IsConnectionReady(HubConnection activeConnection)
        {
            return IsCurrentConnection(activeConnection) && activeConnection.State == HubConnectionState.Connected;
        }

        public async void SendHistory()
        {
            var activeConnection = GetConnectedConnectionOrNotify();
            if (activeConnection == null)
            {
                return;
            }
            if (RoomUsers.Count <= 1)
            {
                ShowMessage("无设备连接");
                return;
            }

            var overlay = await ShowOverlayDialog();
            var historyList = await DatabaseHelper.GetHistory();
            if (historyList.Count == 0)
            {
                ShowMessage("暂无历史记录");
                return;
            }
            var items = new List<HistoryJsonItem>();
            foreach (var item in historyList)
            {
                var siteId = "";
                switch (item.SiteName)
                {
                    case "哔哩哔哩直播":
                        siteId = "bilibili";
                        break;
                    case "斗鱼直播":
                        siteId = "douyu";
                        break;
                    case "虎牙直播":
                        siteId = "huya";
                        break;
                    case "抖音直播":
                        siteId = "douyin";
                        break;
                }

                items.Add(new HistoryJsonItem()
                {
                    SiteId = siteId,
                    Id = $"{siteId}_{item.RoomID}",
                    RoomId = item.RoomID,
                    UserName = item.UserName,
                    Face = item.Photo,
                    UpdateTime = item.WatchTime.ToString("yyyy-MM-dd HH:mm:ss.M"),
                });
            }
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
            var resp = await activeConnection.InvokeAsync<Resp<int>>("SendHistory", RoomID, overlay, json);
            if (resp.IsSuccess)
            {
                ShowMessage("发送成功");
            }
            else
            {
                ShowMessage(resp.Message);
            }
        }

        public async void SendShieldWord()
        {
            var activeConnection = GetConnectedConnectionOrNotify();
            if (activeConnection == null)
            {
                return;
            }
            if (RoomUsers.Count <= 1)
            {
                ShowMessage("无设备连接");
                return;
            }

            var overlay = await ShowOverlayDialog();
            var currentWords = JsonConvert.DeserializeObject<List<string>>(SettingHelper.GetValue<string>(SettingHelper.LiveDanmaku.SHIELD_WORD, "[]"));
            if (currentWords.Count == 0)
            {
                ShowMessage("暂无屏蔽关键词");
                return;
            }
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(currentWords);
            var resp = await activeConnection.InvokeAsync<Resp<int>>("SendShieldWord", RoomID, overlay, json);
            if (resp.IsSuccess)
            {
                ShowMessage("发送成功");
            }
            else
            {
                ShowMessage(resp.Message);
            }
        }

        public async void SendBiliBili()
        {
            var activeConnection = GetConnectedConnectionOrNotify();
            if (activeConnection == null)
            {
                return;
            }
            if (RoomUsers.Count <= 1)
            {
                ShowMessage("无设备连接");
                return;
            }


            var cookie = SettingHelper.GetValue<string>(SettingHelper.BILI_COOKIE, "");
            if (cookie == "")
            {
                ShowMessage("未登录哔哩哔哩账号");
                return;
            }

            var resp = await activeConnection.InvokeAsync<Resp<int>>("SendBiliAccount", RoomID, true, JsonConvert.SerializeObject(new {
                cookie= cookie
            }));
            if (resp.IsSuccess)
            {
                ShowMessage("发送成功");
            }
            else
            {
                ShowMessage(resp.Message);
            }
        }

        private async Task<bool> ShowOverlayDialog()
        {
            var dialog = new MessageDialog("是否覆盖远端数据？", "覆盖数据");
            dialog.Commands.Add(new UICommand("是", null, true));
            dialog.Commands.Add(new UICommand("否", null, false));
            var result = await dialog.ShowAsync();
            return (bool)result.Id;
        }

        private HubConnection GetConnectedConnectionOrNotify()
        {
            var activeConnection = connection;
            if (activeConnection == null || activeConnection.State != HubConnectionState.Connected)
            {
                ShowMessage("连接已断开");
                return null;
            }
            return activeConnection;
        }

        private bool IsCurrentConnection(HubConnection activeConnection)
        {
            return activeConnection != null && ReferenceEquals(connection, activeConnection);
        }



        private class SyncDataBatch
        {
            public string Content { get; set; }
            public int ItemCount { get; set; }
            public int InvocationBytes { get; set; }
        }

        private class SyncPayloadTooLargeException : Exception
        {
            public SyncPayloadTooLargeException(string message) : base(message)
            {
            }
        }

    }
    public class Resp<T>
    {
        public bool IsSuccess { get; set; } = true;
        public string Message { get; set; } = "";

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public T Data { get; set; }

        public static Resp<T> Error(string message)
        {
            return new Resp<T> { IsSuccess = false, Message = message };
        }

        public static Resp<T> Success(T data)
        {
            return new Resp<T> { IsSuccess = true, Data = data };
        }
    }

    public class RoomUser
    {
        public string ConnectionId { get; set; } = "";
        public string ShortId { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Version { get; set; } = "";
        public string App { get; set; } = "";

        public bool IsCreator { get; set; } = false;

        public bool IsCurrentUser { get; set; } = false;
    }

    public class HistoryJsonItem
    {
        [JsonProperty("siteId")]
        public string SiteId;

        [JsonProperty("id")]
        public string Id;

        [JsonProperty("roomId")]
        public string RoomId;

        [JsonProperty("userName")]
        public string UserName;

        [JsonProperty("face")]
        public string Face;

        [JsonProperty("updateTime")]
        public string UpdateTime;

        [JsonIgnore]
        public string SiteName
        {
            get
            {
                switch (SiteId)
                {
                    case "bilibili":
                        return "哔哩哔哩直播";
                    case "douyu":
                        return "斗鱼直播";
                    case "huya":
                        return "虎牙直播";
                    case "douyin":
                        return "抖音直播";
                    default:
                        return "未知";
                }
            }
        }

    }
}
