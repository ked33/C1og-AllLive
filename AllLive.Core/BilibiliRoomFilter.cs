using Newtonsoft.Json.Linq;
using System;

namespace AllLive.Core
{
    /// <summary>
    /// 哔哩哔哩隐藏/加密房间过滤：基于 room/v1/Room/room_init 的 data 判断房间是否应对关注列表隐藏。
    /// 这类房间在 room/v1/Room/get_info 中仍可能返回 live_status=1，需要在检测开播状态时排除。
    /// </summary>
    public static class BilibiliRoomFilter
    {
        /// <summary>
        /// 判断房间是否应被过滤（隐藏/锁定/未通过密码验证的加密房间）。
        /// 返回 true 表示过滤；返回 false 表示不过滤；返回 null 表示无法判断（data 缺失或异常），调用方应保持原状态。
        /// </summary>
        public static bool? ShouldFilter(JToken roomInitData)
        {
            if (roomInitData == null || roomInitData.Type != JTokenType.Object)
            {
                return null;
            }

            var isHidden = ToFlag(roomInitData["is_hidden"]);
            var isLocked = ToFlag(roomInitData["is_locked"]);
            var encrypted = ToFlag(roomInitData["encrypted"]);
            var pwdVerified = ToFlag(roomInitData["pwd_verified"]);
            return isHidden || isLocked || (encrypted && !pwdVerified);
        }

        /// <summary>
        /// 兼容 bool 与 0/1（数字或字符串）两种返回形态。
        /// </summary>
        private static bool ToFlag(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Integer:
                    return token.Value<long>() != 0;
                case JTokenType.Float:
                    return token.Value<double>() != 0;
                default:
                    var text = token.ToString();
                    return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
