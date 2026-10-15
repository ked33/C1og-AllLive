using Newtonsoft.Json.Linq;
using System;
using System.Globalization;

namespace AllLive.Core
{
    internal static class LiveStatusParser
    {
        // Missing/changed API fields mean unknown, never a confirmed offline room.
        // 诊断信息携带原始状态值，便于后续从日志定位平台新增/变更的状态码。
        internal static int ReadRequiredNumber(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.String) ||
                !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
                throw new InvalidOperationException("Live status is missing or invalid: " + DescribeToken(token));
            return status;
        }

        internal static bool IsLive(JToken token, int liveStatus, params int[] offlineStatuses)
        {
            var status = ReadRequiredNumber(token);
            if (status == liveStatus) return true;
            foreach (var offline in offlineStatuses)
                if (status == offline) return false;
            throw new InvalidOperationException("Live status is unknown: " + status);
        }

        internal static string DescribeToken(JToken token)
        {
            if (token == null)
            {
                return "<missing>";
            }
            if (token.Type == JTokenType.Null)
            {
                return "<null>";
            }
            return token.Type + ":" + token;
        }

        internal static bool IsTwitchLive(JToken stream)
        {
            if (stream is JObject) return true;
            if (stream?.Type == JTokenType.Null) return false;
            throw new InvalidOperationException("Live stream status is missing or invalid.");
        }
    }
}
