using AllLive.UWP.Helper;
using AllLive.UWP.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace AllLive.Tests
{
    public static class HlsPreferenceStoreTests
    {
        private const string Site = "哔哩哔哩直播";

        public static Task<string> RunAsync()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values.Clear();
            var fast = new PlayurlLine { Codec = "AVC", Url = "https://cn-jssz-cm-02-08.bilivideo.com/live/x.m3u8?secret=old" };
            var slow = new PlayurlLine { Codec = "AVC", Url = "https://d1--cn-gotcha204.bilivideo.com/live/x.m3u8" };
            PlaybackCdnPreferenceStore.Remember(Site, "25923197", fast);
            fast.Url = "https://cn-jssz-cm-02-08.bilivideo.com/live/new.m3u8?secret=new";
            var lines = new List<PlayurlLine> { slow, fast };
            Assert(PlaybackCdnPreferenceStore.Order(Site, "25923197", lines)[0] == fast, "Renewed URL did not match saved host.");
            Assert(PlaybackCdnPreferenceStore.Order(Site, "100", lines)[0] == slow, "Preference crossed rooms.");
            Assert(PlaybackCdnPreferenceStore.Order("其它直播", "25923197", lines)[0] == slow, "Preference crossed platforms.");
            var record = (ApplicationDataCompositeValue)values["25923197:AVC"];
            Assert(record.Count == 2 && (string)record["Host"] == "cn-jssz-cm-02-08.bilivideo.com", "Stored unnecessary URL or credential data.");

            var hevc = new PlayurlLine { Codec = "HEVC", Url = fast.Url };
            PlaybackCdnPreferenceStore.Remember(Site, "25923197", hevc);
            Assert(PlaybackCdnPreferenceStore.Order(Site, "25923197", new List<PlayurlLine> { slow, hevc })[0] == slow, "Preference changed codec.");
            PlaybackCdnPreferenceStore.Forget(Site, "25923197", slow);
            Assert(values.ContainsKey("25923197:AVC"), "Failure on another host removed the good preference.");
            PlaybackCdnPreferenceStore.Forget(Site, "25923197", fast);
            Assert(!values.ContainsKey("25923197:AVC"), "Failed host preference survived.");

            foreach (var ticks in new[] { DateTimeOffset.UtcNow.AddDays(-8).UtcDateTime.Ticks, DateTimeOffset.UtcNow.AddDays(1).UtcDateTime.Ticks, long.MinValue })
            {
                values["25923197:AVC"] = new ApplicationDataCompositeValue { ["Host"] = "cn-jssz-cm-02-08.bilivideo.com", ["SavedUtcTicks"] = ticks };
                Assert(PlaybackCdnPreferenceStore.Order(Site, "25923197", lines)[0] == slow, "Expired/future/corrupt preference was used.");
            }
            values.Clear();
            for (var i = 1; i <= 130; i++) PlaybackCdnPreferenceStore.Remember(Site, i.ToString(), fast);
            Assert(values.Count == 128 && values.ContainsKey("130:AVC") && !values.ContainsKey("1:AVC"), "Cache is not bounded/oldest-first.");
            values.Clear();
            return Task.FromResult("PASS: CDN preference renewal, room/codec isolation, TTL, invalidation and bounded storage.");
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("HLS preference: " + message);
        }
    }
}

// In-memory substitutes for policy tests, not WinRT validation. The real storage
// binding and page integration are compiled by the UWP Actions build.
namespace Windows.Storage
{
    public enum ApplicationDataCreateDisposition { Always }
    public sealed class ApplicationDataCompositeValue : Dictionary<string, object> { }
    public sealed class ApplicationDataContainer
    {
        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>();
        public ApplicationDataContainer CreateContainer(string name, ApplicationDataCreateDisposition disposition) { return this; }
    }
    public sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new ApplicationData();
        public ApplicationDataContainer LocalSettings { get; } = new ApplicationDataContainer();
    }
}
namespace AllLive.UWP.ViewModels
{
    public sealed class PlayurlLine
    {
        public string Url { get; set; }
        public string Codec { get; set; }
    }
}
namespace AllLive.UWP.Helper
{
    public static class LogHelper
    {
        public static void LogDebug(Func<string> message) { }
    }
}
