using System;
using System.Collections.Generic;
using System.Globalization;
using AllLive.UWP.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AllLive.UWP.Helper
{
    // Parse an entire batch before a replacing import is allowed to touch the database.
    internal static class DataImportParser
    {
        internal static List<FavoriteItem> ParseFavorites(string content)
        {
            var result = new List<FavoriteItem>();
            foreach (var token in ParseArray(content))
            {
                var item = RequireObject(token);
                result.Add(new FavoriteItem
                {
                    SiteName = ParseSiteName(item), RoomID = ParseRoomId(item),
                    UserName = ReadOptionalText(item, "userName"), Photo = ReadOptionalText(item, "face"),
                    SortOrder = ParseSortOrder(item)
                });
            }
            return result;
        }

        internal static List<HistoryItem> ParseHistory(string content)
        {
            var result = new List<HistoryItem>();
            foreach (var token in ParseArray(content))
            {
                var item = RequireObject(token);
                DateTime watchTime;
                var text = ReadOptionalText(item, "updateTime");
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out watchTime)
                    && !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.RoundtripKind, out watchTime))
                {
                    throw new JsonSerializationException("历史记录的观看时间无效");
                }
                if (watchTime == DateTime.MinValue) throw new JsonSerializationException("历史记录的观看时间无效");
                // Existing databases and the history UI use local wall-clock time. RoundtripKind
                // preserves a Z timestamp as UTC, so normalize it before SQLite drops DateTime.Kind.
                if (watchTime.Kind == DateTimeKind.Utc) watchTime = watchTime.ToLocalTime();
                result.Add(new HistoryItem
                {
                    SiteName = ParseSiteName(item), RoomID = ParseRoomId(item),
                    UserName = ReadOptionalText(item, "userName"), Photo = ReadOptionalText(item, "face"),
                    WatchTime = watchTime
                });
            }
            return result;
        }

        private static JArray ParseArray(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new JsonSerializationException("导入内容为空");
            // DateParseHandling.None keeps the original time text, including sub-second precision.
            using (var reader = new JsonTextReader(new System.IO.StringReader(content)) { DateParseHandling = DateParseHandling.None })
            {
                var result = JArray.Load(reader);
                if (reader.Read()) throw new JsonSerializationException("导入内容包含多余数据");
                return result;
            }
        }

        private static JObject RequireObject(JToken token)
        {
            var item = token as JObject;
            if (item == null) throw new JsonSerializationException("导入列表包含空条目或格式无效的条目");
            return item;
        }

        private static string ParseSiteName(JObject item)
        {
            switch (ReadOptionalText(item, "siteId"))
            {
                case "bilibili": return "哔哩哔哩直播";
                case "douyu": return "斗鱼直播";
                case "huya": return "虎牙直播";
                case "douyin": return "抖音直播";
                case "twitch": return "Twitch直播";
                default: throw new JsonSerializationException("导入条目的平台标识无效或暂不支持");
            }
        }

        private static string ParseRoomId(JObject item)
        {
            var roomId = ReadOptionalText(item, "roomId");
            if (string.IsNullOrWhiteSpace(roomId)) throw new JsonSerializationException("导入条目的房间标识无效");
            return roomId;
        }

        private static int ParseSortOrder(JObject item)
        {
            var token = item.GetValue("sort", StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null) return 0;
            int sortOrder;
            if ((token.Type != JTokenType.Integer && token.Type != JTokenType.String)
                || !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sortOrder))
            {
                throw new JsonSerializationException("关注条目的排序值无效");
            }
            return sortOrder;
        }

        private static string ReadOptionalText(JObject item, string name)
        {
            // JsonConvert's DTO property matching was case-insensitive; retain that import format.
            var token = item.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            if (token.Type != JTokenType.String && token.Type != JTokenType.Integer)
            {
                throw new JsonSerializationException("导入条目的字段格式无效");
            }
            return token.ToString();
        }
    }
}
