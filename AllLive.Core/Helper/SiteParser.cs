using AllLive.Core.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AllLive.Core.Helper;
using WebSocketSharp;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Web;

namespace AllLive.UWP.Helper
{
    public enum LiveSite
    {
        Bilibili=0,
        Douyu=1,
        Huya=2,
        Douyin=3,
        Unknown=99,
    }
    public class SiteParser
    {
        public static async Task<(LiveSite, string)> ParseUrl(string url)
        {
            var normalizedInput = NormalizeInput(url);
            if (string.IsNullOrWhiteSpace(normalizedInput))
            {
                return (LiveSite.Unknown, "");
            }

            if (TryCreateAbsoluteUri(normalizedInput, out var uri))
            {
                var parsed = await ParseUri(uri);
                if (parsed.Item1 != LiveSite.Unknown && !string.IsNullOrWhiteSpace(parsed.Item2))
                {
                    return parsed;
                }
            }

            var embeddedUrl = ExtractEmbeddedUrl(normalizedInput);
            if (!string.IsNullOrWhiteSpace(embeddedUrl) && TryCreateAbsoluteUri(embeddedUrl, out uri))
            {
                var parsed = await ParseUri(uri);
                if (parsed.Item1 != LiveSite.Unknown && !string.IsNullOrWhiteSpace(parsed.Item2))
                {
                    return parsed;
                }
            }

            return ParseByLegacyRegex(normalizedInput);
        }

        private static async Task<(LiveSite, string)> ParseUri(Uri uri)
        {
            if (uri == null)
            {
                return (LiveSite.Unknown, "");
            }

            var host = uri.Host?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host))
            {
                return (LiveSite.Unknown, "");
            }

            if (host.EndsWith("b23.tv", StringComparison.OrdinalIgnoreCase))
            {
                var location = await GetLocation(uri.AbsoluteUri);
                return await ParseUrl(location);
            }

            if (host.EndsWith("v.douyin.com", StringComparison.OrdinalIgnoreCase))
            {
                var location = await GetLocation(uri.AbsoluteUri);
                return await ParseUrl(location);
            }

            if (host.Contains("webcast.amemv.com"))
            {
                return (LiveSite.Douyin, GetFirstRegexMatch(uri.AbsoluteUri, @"reflow/(\d+)"));
            }

            if (host.Contains("live.douyin.com"))
            {
                return (LiveSite.Douyin, GetLastMeaningfulSegment(uri));
            }

            if (host.Contains("douyu.com"))
            {
                var roomId = string.Empty;
                if (uri.AbsolutePath.IndexOf("/topic/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    roomId = GetQueryValue(uri, "rid");
                }
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = GetLastMeaningfulSegment(uri);
                }
                return (LiveSite.Douyu, roomId);
            }

            if (host.Contains("huya.com"))
            {
                return (LiveSite.Huya, GetLastMeaningfulSegment(uri));
            }

            if (host.Contains("bilibili.com"))
            {
                var roomId = GetQueryValue(uri, "room_id", "roomid", "id");
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = GetLastMeaningfulSegment(uri, "live", "blanc", "h5");
                }
                return (LiveSite.Bilibili, roomId);
            }

            return (LiveSite.Unknown, "");
        }

        private static (LiveSite, string) ParseByLegacyRegex(string input)
        {
            var roomId = "";
            var site = LiveSite.Unknown;

            if (input.Contains("bilibili.com"))
            {
                roomId = input.MatchText(@"bilibili\.com/([\d|\w]+)", "");
                site = LiveSite.Bilibili;
            }

            if (input.Contains("douyu.com"))
            {
                if (input.Contains("topic"))
                {
                    roomId = GetFirstRegexMatch(input, @"[?&]rid=(\d+)");
                }
                if (string.IsNullOrWhiteSpace(roomId))
                {
                    roomId = input.MatchText(@"douyu\.com/([\d|\w]+)", "");
                }
                site = LiveSite.Douyu;
            }

            if (input.Contains("huya.com"))
            {
                roomId = input.MatchText(@"huya\.com/([\d|\w]+)", "");
                site = LiveSite.Huya;
            }

            if (input.Contains("live.douyin.com"))
            {
                roomId = input.MatchText(@"live\.douyin\.com/([\d|\w]+)", "");
                site = LiveSite.Douyin;
            }

            if (input.Contains("webcast.amemv.com"))
            {
                roomId = input.MatchText(@"reflow/(\d+)", "");
                site = LiveSite.Douyin;
            }

            return (site, roomId);
        }

        private static string NormalizeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var normalized = input.Trim();
            normalized = normalized.Replace("\r", " ").Replace("\n", " ");
            normalized = normalized.Replace("\u200B", string.Empty).Replace("\uFEFF", string.Empty);
            normalized = normalized.Trim().Trim('"', '\'', '“', '”', '‘', '’');
            return normalized;
        }

        private static string ExtractEmbeddedUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var match = Regex.Match(input, @"https?://[^\s]+", RegexOptions.IgnoreCase);
            return match.Success ? match.Value.TrimEnd(')', ']', '}', ',', '，', '。') : null;
        }

        private static bool TryCreateAbsoluteUri(string input, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var candidate = input.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out uri))
            {
                return true;
            }

            if (candidate.IndexOf("://", StringComparison.OrdinalIgnoreCase) < 0
                && Regex.IsMatch(candidate, @"^(?:[\w-]+\.)+[\w-]+(?:/.*)?$", RegexOptions.IgnoreCase))
            {
                return Uri.TryCreate("https://" + candidate, UriKind.Absolute, out uri);
            }

            return false;
        }

        private static string GetQueryValue(Uri uri, params string[] names)
        {
            if (uri == null || string.IsNullOrWhiteSpace(uri.Query) || names == null)
            {
                return string.Empty;
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var value = query[name];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }

        private static string GetLastMeaningfulSegment(Uri uri, params string[] ignoredSegments)
        {
            if (uri == null)
            {
                return string.Empty;
            }

            var ignored = new HashSet<string>(ignoredSegments ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var segments = uri.AbsolutePath
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Uri.UnescapeDataString(x ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            for (var i = segments.Count - 1; i >= 0; i--)
            {
                var segment = segments[i];
                if (ignored.Contains(segment))
                {
                    continue;
                }
                if (!Regex.IsMatch(segment, @"^[0-9A-Za-z_-]+$"))
                {
                    continue;
                }
                return segment;
            }

            return string.Empty;
        }

        private static string GetFirstRegexMatch(string input, string pattern)
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(pattern))
            {
                return string.Empty;
            }

            var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }


        private static async Task<string> GetLocation(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return "";
                }
                using (var headResp = await HttpUtil.Head(url))
                {
                    if (headResp.Headers.Location != null)
                    {
                        return headResp.Headers.Location.ToString();
                    }
                }
              
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            return "";
        }

    }
}
