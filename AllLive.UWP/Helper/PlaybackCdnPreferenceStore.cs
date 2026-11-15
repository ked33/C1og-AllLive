using AllLive.Core;
using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace AllLive.UWP.Helper
{
    internal static class PlaybackCdnPreferenceStore
    {
        private static readonly object Sync = new object();
        private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
        private const int MaxEntries = 128;

        private static string Key(string site, string room, string codec)
        {
            // Canonical Bilibili room IDs are numeric. Keep stored keys small and credential-free.
            return site == "哔哩哔哩直播" && !string.IsNullOrEmpty(room) && room.Length <= 32 &&
                room.All(char.IsDigit) && (codec == "AVC" || codec == "HEVC" || codec == "AV1")
                ? room + ":" + codec : null;
        }

        private static ApplicationDataContainer Container()
        {
            return ApplicationData.Current.LocalSettings.CreateContainer(
                "PlaybackCdnPreferencesV1", ApplicationDataCreateDisposition.Always);
        }

        private static bool Read(object value, DateTimeOffset now, out string host, out long ticks)
        {
            host = null;
            ticks = 0;
            var item = value as ApplicationDataCompositeValue;
            object savedHost, savedAt;
            if (item == null || !item.TryGetValue("Host", out savedHost) ||
                !item.TryGetValue("SavedUtcTicks", out savedAt) || !(savedAt is long)) return false;
            host = savedHost as string;
            ticks = (long)savedAt;
            return host != null && host.Length <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns &&
                ticks > 0 && ticks <= now.UtcDateTime.Ticks && now.UtcDateTime.Ticks - ticks <= Lifetime.Ticks;
        }

        public static List<PlayurlLine> Order(string site, string room, List<PlayurlLine> lines)
        {
            if (lines == null || lines.Count == 0) return lines;
            // Keep the current codec policy. A remembered HEVC host must never select HEVC over AVC.
            var codec = lines[0].Codec;
            var key = Key(site, room, codec);
            if (key == null) return lines;
            try
            {
                lock (Sync)
                {
                    var values = Container().Values;
                    object value;
                    string host;
                    long ticks;
                    if (!values.TryGetValue(key, out value)) return lines;
                    if (!Read(value, DateTimeOffset.UtcNow, out host, out ticks))
                    {
                        values.Remove(key);
                        return lines;
                    }
                    var ordered = lines.OrderBy(x => x.Codec == codec ? HlsCdnIdentity.PreferenceRank(x.Url, host) : 2).ToList();
                    if (!ReferenceEquals(ordered[0], lines[0]))
                        LogHelper.LogDebug(() => $"[HlsHealth] event=PreferredCdn room={room} codec={codec} host={HlsCdnIdentity.GetHost(ordered[0].Url)}");
                    return ordered;
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogDebug(() => "[HlsHealth] event=PreferenceReadFailed error=" + ex.GetType().Name);
                return lines;
            }
        }

        public static void Remember(string site, string room, PlayurlLine line)
        {
            var key = Key(site, room, line?.Codec);
            var host = HlsCdnIdentity.GetHost(line?.Url);
            if (key == null || host == null) return;
            try
            {
                lock (Sync)
                {
                    var values = Container().Values;
                    var now = DateTimeOffset.UtcNow;
                    var valid = new List<Tuple<string, long>>();
                    foreach (var pair in values.ToList())
                    {
                        string oldHost;
                        long ticks;
                        if (!Read(pair.Value, now, out oldHost, out ticks)) values.Remove(pair.Key);
                        else if (pair.Key != key) valid.Add(Tuple.Create(pair.Key, ticks));
                    }
                    foreach (var old in valid.OrderByDescending(x => x.Item2).Skip(MaxEntries - 1)) values.Remove(old.Item1);
                    values[key] = new ApplicationDataCompositeValue
                    {
                        ["Host"] = host,
                        ["SavedUtcTicks"] = now.UtcDateTime.Ticks
                    };
                    LogHelper.LogDebug(() => $"[HlsHealth] event=CdnRemembered room={room} codec={line.Codec} host={host} ttlDays=7");
                }
            }
            catch (Exception ex) { LogHelper.LogDebug(() => "[HlsHealth] event=PreferenceWriteFailed error=" + ex.GetType().Name); }
        }

        public static void Forget(string site, string room, PlayurlLine line)
        {
            var key = Key(site, room, line?.Codec);
            var host = HlsCdnIdentity.GetHost(line?.Url);
            if (key == null || host == null) return;
            try
            {
                lock (Sync)
                {
                    var values = Container().Values;
                    object value;
                    string oldHost;
                    long ticks;
                    if (values.TryGetValue(key, out value) && Read(value, DateTimeOffset.UtcNow, out oldHost, out ticks) && oldHost == host)
                        values.Remove(key);
                }
            }
            catch (Exception ex) { LogHelper.LogDebug(() => "[HlsHealth] event=PreferenceRemoveFailed error=" + ex.GetType().Name); }
        }
    }
}
