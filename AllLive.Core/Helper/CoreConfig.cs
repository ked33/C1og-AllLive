using System;
using System.Collections.Generic;
using System.Linq;

namespace AllLive.Core.Helper
{
    public static class CoreConfig
    {
        private static readonly object _lock = new object();
        private static List<string> _douyuSignServiceUrls = new List<string>();
        private static List<string> _douyinSignServiceUrls = new List<string>();
        private static string _douyinCookie = "";
        private static string _douyuDeviceId = "";
        private static bool _bilibiliFilterHiddenRooms = true;

        public static IReadOnlyList<string> GetDouyuSignServiceUrls()
        {
            lock (_lock)
            {
                return _douyuSignServiceUrls.ToList();
            }
        }

        public static void SetDouyuSignServiceUrls(IEnumerable<string> urls)
        {
            var list = (urls ?? Enumerable.Empty<string>())
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            lock (_lock)
            {
                _douyuSignServiceUrls = list;
            }
        }

        public static void SetDouyuSignServiceUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SetDouyuSignServiceUrls(Array.Empty<string>());
                return;
            }
            var urls = value
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
            SetDouyuSignServiceUrls(urls);
        }

        public static IReadOnlyList<string> GetDouyinSignServiceUrls()
        {
            lock (_lock)
            {
                return _douyinSignServiceUrls.ToList();
            }
        }

        public static void SetDouyinSignServiceUrls(IEnumerable<string> urls)
        {
            var list = (urls ?? Enumerable.Empty<string>())
                .Select(x => x?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            lock (_lock)
            {
                _douyinSignServiceUrls = list;
            }
        }

        public static void SetDouyinSignServiceUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SetDouyinSignServiceUrls(Array.Empty<string>());
                return;
            }
            var urls = value
                .Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());
            SetDouyinSignServiceUrls(urls);
        }

        public static string GetDouyinCookie()
        {
            lock (_lock)
            {
                return _douyinCookie ?? "";
            }
        }

        public static void SetDouyinCookie(string value)
        {
            var cookie = value?.Trim() ?? "";
            lock (_lock)
            {
                _douyinCookie = cookie;
            }
        }

        public static string GetDouyuDeviceId()
        {
            lock (_lock)
            {
                return _douyuDeviceId ?? "";
            }
        }

        public static void SetDouyuDeviceId(string value)
        {
            var deviceId = value?.Trim() ?? "";
            lock (_lock)
            {
                _douyuDeviceId = deviceId;
            }
        }

        /// <summary>
        /// 哔哩哔哩隐藏/加密房间过滤（默认开启）：开启后这类房间视为未开播，避免误报开播。
        /// </summary>
        public static bool GetBilibiliFilterHiddenRooms()
        {
            lock (_lock)
            {
                return _bilibiliFilterHiddenRooms;
            }
        }

        public static void SetBilibiliFilterHiddenRooms(bool value)
        {
            lock (_lock)
            {
                _bilibiliFilterHiddenRooms = value;
            }
        }
    }
}
