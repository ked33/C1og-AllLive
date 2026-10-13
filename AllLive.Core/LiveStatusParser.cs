using Newtonsoft.Json.Linq;
using System;
using System.Globalization;

namespace AllLive.Core
{
    internal static class LiveStatusParser
    {
        // Missing/changed API fields mean unknown, never a confirmed offline room.
        internal static int ReadRequiredNumber(JToken token)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.String) ||
                !int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
                throw new InvalidOperationException("Live status is missing or invalid.");
            return status;
        }

        internal static bool IsLive(JToken token, int liveStatus, params int[] offlineStatuses)
        {
            var status = ReadRequiredNumber(token);
            if (status == liveStatus) return true;
            foreach (var offline in offlineStatuses)
                if (status == offline) return false;
            throw new InvalidOperationException("Live status is unknown.");
        }

        internal static bool IsTwitchLive(JToken stream)
        {
            if (stream is JObject) return true;
            if (stream?.Type == JTokenType.Null) return false;
            throw new InvalidOperationException("Live stream status is missing or invalid.");
        }
    }
}
