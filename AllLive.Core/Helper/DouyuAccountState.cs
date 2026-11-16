using System;
using System.Collections.Generic;
using System.Linq;

namespace AllLive.Core.Helper
{
    // An immutable snapshot keeps signing and HTTP headers on the same account/device.
    public sealed class DouyuAccountSession
    {
        public string Cookie { get; }
        public string DeviceId { get; }
        public long Revision { get; }
        public bool HasLogin { get; }
        public string UserName { get; }

        internal DouyuAccountSession(string cookie, string anonymousDeviceId, long revision)
        {
            var fields = ParseCookies(cookie);
            DeviceId = Get(fields, "dy_did") ?? Get(fields, "acf_did") ?? anonymousDeviceId;
            fields["dy_did"] = DeviceId;
            fields["acf_did"] = DeviceId;
            Cookie = FormatCookies(fields);
            Revision = revision;
            HasLogin = Get(fields, "dy_auth") != null ||
                (Get(fields, "acf_auth") != null && Get(fields, "acf_jwt_token") != null);
            var name = Get(fields, "acf_nickname") ?? Get(fields, "acf_username") ?? "";
            try { UserName = Uri.UnescapeDataString(name.Replace("+", " ")); }
            catch (UriFormatException) { UserName = ""; }
        }

        public string PlaybackCookie
        {
            get
            {
                var fields = ParseCookies(Cookie);
                var h5 = Get(fields, "acf_auth") != null && Get(fields, "acf_jwt_token") != null;
                var result = new Dictionary<string, string> { ["dy_did"] = DeviceId, ["acf_did"] = DeviceId };
                if (h5)
                {
                    foreach (var pair in fields.Where(x => x.Key.StartsWith("acf_", StringComparison.Ordinal)))
                        result[pair.Key] = pair.Value;
                }
                else if (Get(fields, "dy_auth") != null) result["dy_auth"] = fields["dy_auth"];
                // Never mix Web/H5 sessions or send the passport renewal key to playback APIs.
                return FormatCookies(result);
            }
        }

        public static bool ContainsLogin(string cookie) => new DouyuAccountSession(cookie, "", 0).HasLogin;

        internal static Dictionary<string, string> ParseCookies(string raw)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(raw)) return fields;
            if (raw.Length > 65536 || raw.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("斗鱼登录数据格式无效");
            foreach (var part in raw.Split(';'))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0) continue;
                var key = part.Substring(0, separator).Trim();
                var value = part.Substring(separator + 1).Trim();
                if (key != "dy_auth" && key != "dy_did" && key != "LTP0" &&
                    !key.StartsWith("acf_", StringComparison.Ordinal)) continue;
                if (!key.All(c => char.IsLetterOrDigit(c) || c == '_')) continue;
                if (value.Length != 0) fields[key] = value;
            }
            return fields;
        }

        internal static string FormatCookies(IDictionary<string, string> fields) =>
            string.Join("; ", fields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));

        private static string Get(IDictionary<string, string> fields, string key) =>
            fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    public static class DouyuAccountState
    {
        private static readonly object gate = new object();
        private static DouyuAccountSession current;
        private static string anonymousDeviceId;

        public static DouyuAccountSession Capture()
        {
            lock (gate)
            {
                if (current == null)
                {
                    anonymousDeviceId = CoreConfig.GetDouyuDeviceId();
                    if (string.IsNullOrWhiteSpace(anonymousDeviceId)) anonymousDeviceId = Guid.NewGuid().ToString("N");
                    current = new DouyuAccountSession("", anonymousDeviceId, 0);
                }
                return current;
            }
        }

        public static bool IsCurrent(DouyuAccountSession session) => ReferenceEquals(Capture(), session);

        public static void SetCookie(string cookie)
        {
            lock (gate)
            {
                var previous = Capture();
                current = new DouyuAccountSession(cookie, anonymousDeviceId, previous.Revision + 1);
            }
        }
    }
}
