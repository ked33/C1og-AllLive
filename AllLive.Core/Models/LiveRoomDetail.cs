using System;
using System.Collections.Generic;
using System.Text;

namespace AllLive.Core.Models
{
    public class LiveRoomDetail
    {
        /// <summary>
        /// 房间号
        /// </summary>
        public string RoomID { get; set; }
        /// <summary>
        /// 房间标题
        /// </summary>
        public string Title { get; set; }
        /// <summary>
        /// 封面
        /// </summary>
        public string Cover { get; set; }
        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; }
        /// <summary>
        /// 用户头像
        /// </summary>
        public string UserAvatar { get; set; }
        /// <summary>
        /// 兼容旧逻辑的统计值，建议新逻辑优先使用 ViewerCount / VipCount / Popularity
        /// </summary>
        public int Online { get; set; }
        /// <summary>
        /// 直播间在线人数
        /// </summary>
        public long? ViewerCount { get; set; }
        /// <summary>
        /// 人气/热度
        /// </summary>
        public long? Popularity { get; set; }
        /// <summary>
        /// 贵宾数
        /// </summary>
        public long? VipCount { get; set; }
        /// <summary>
        /// 在线人数来源
        /// </summary>
        public string ViewerCountSource { get; set; }
        /// <summary>
        /// 人气/热度来源
        /// </summary>
        public string PopularitySource { get; set; }
        /// <summary>
        /// 是否允许在没有真实在线人数时显示人气/热度
        /// </summary>
        public bool AllowPopularityFallback { get; set; } = true;
        /// <summary>
        /// 贵宾数来源
        /// </summary>
        public string VipCountSource { get; set; }
        /// <summary>
        /// 房间介绍
        /// </summary>
        public string Introduction { get; set; }
        /// <summary>
        /// 房间公告
        /// </summary>
        public string Notice { get; set; }
        /// <summary>
        /// 直播状态
        /// </summary>
        public bool Status { get; set; }
        /// <summary>
        /// 一些其他信息
        /// </summary>
        public object Data { get; set; }
        /// <summary>
        /// 弹幕数据
        /// </summary>
        public object DanmakuData { get; set; }
        /// <summary>
        /// 链接
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// 是否录播
        /// </summary>
        public bool IsRecord { get; set; } = false;
    }
}
