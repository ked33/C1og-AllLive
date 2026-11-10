using AllLive.Core.Helper;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace AllLive.Core.Models
{
    public enum LiveMessageType
    {
        /// <summary>
        /// 聊天
        /// </summary>
        Chat,
        /// <summary>
        /// 礼物,暂时不支持
        /// </summary>
        Gift,
        /// <summary>
        /// 房间统计值
        /// </summary>
        Online,
        /// <summary>
        /// 醒目留言
        /// </summary>
        SuperChat
    }

    public enum LiveAudienceMetricKind
    {
        Unknown,
        ViewerCount,
        VipCount,
        Popularity
    }

    public class LiveMessage
    {
        /// <summary>
        /// 消息类型
        /// </summary>
        public LiveMessageType Type { get; set; }
        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; }
        /// <summary>
        /// 信息
        /// </summary>
        public string Message { get; set; }
        /// <summary>
        /// 数据
        /// 单Type=Online时，Data 为房间统计值(long)，具体语义由平台决定
        /// </summary>
        public object Data { get; set; }
        /// <summary>
        /// 单Type=Online时，标记 Data 是真实观众数还是平台热度/人气
        /// </summary>
        public LiveAudienceMetricKind AudienceMetricKind { get; set; } = LiveAudienceMetricKind.Unknown;
        /// <summary>
        /// 弹幕颜色
        /// </summary>
        public DanmakuColor Color { get; set; }= DanmakuColor.White;
    }

    public class LiveSuperChatMessage
    {
        public string Id { get; set; }
        public string UserName { get; set; }
        public string Face { get; set; }
        public string Message { get; set; }
        public int Price { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string BackgroundColor { get; set; }
        public string BackgroundBottomColor { get; set; }
    }
}
