using System;
using System.Collections.Generic;
using System.Linq;

namespace AllLive.Core
{
    public sealed class PlaybackHealthObservation
    {
        public bool Stable { get; internal set; }
        public bool NeedsRecovery { get; internal set; }
        public string Reason { get; internal set; }
        public double WindowSeconds { get; internal set; }
        public double WindowProgressSeconds { get; internal set; }
    }

    // No UI, clock or network dependencies: callers supply monotonic elapsed time.
    public sealed class PlaybackHealthTracker
    {
        private readonly Queue<Tuple<double, double>> window = new Queue<Tuple<double, double>>();
        private readonly Queue<double> discontinuities = new Queue<double>();
        private double ageSeconds;
        private double normalSeconds;
        private double stalledSeconds;
        private bool stableReported;

        public void ResetStability()
        {
            window.Clear();
            discontinuities.Clear();
            normalSeconds = stalledSeconds = 0;
            stableReported = false;
        }

        public PlaybackHealthObservation Observe(double wallSeconds, double? progressSeconds, bool eligible)
        {
            var result = new PlaybackHealthObservation { Reason = "Unobserved" };
            if (double.IsNaN(wallSeconds) || double.IsInfinity(wallSeconds) || wallSeconds <= 0)
            {
                ResetStability();
                return result;
            }
            ageSeconds += wallSeconds;
            // A delayed UI callback, pause, background throttling or startup is not a CDN failure.
            if (!eligible || wallSeconds < 1.5 || wallSeconds > 5 || ageSeconds < 12 ||
                !progressSeconds.HasValue || double.IsNaN(progressSeconds.Value) || double.IsInfinity(progressSeconds.Value))
            {
                ResetStability();
                return result;
            }

            var progress = progressSeconds.Value;
            while (discontinuities.Count > 0 && ageSeconds - discontinuities.Peek() > 20)
                discontinuities.Dequeue();
            if (progress < 0 || progress > wallSeconds * 1.5)
            {
                window.Clear();
                normalSeconds = stalledSeconds = 0;
                stableReported = false;
                discontinuities.Enqueue(ageSeconds);
                result.Reason = "TimestampDiscontinuity";
                result.NeedsRecovery = discontinuities.Count >= 3;
                return result;
            }

            window.Enqueue(Tuple.Create(wallSeconds, progress));
            var totalWall = window.Sum(x => x.Item1);
            while (window.Count > 1 && totalWall - window.Peek().Item1 >= 20)
                totalWall -= window.Dequeue().Item1;
            var totalProgress = window.Sum(x => x.Item2);
            var ratio = progress / wallSeconds;
            normalSeconds = ratio >= 0.9 && ratio <= 1.15 ? normalSeconds + wallSeconds : 0;
            stalledSeconds = ratio < 0.2 ? stalledSeconds + wallSeconds : 0;
            if (normalSeconds == 0) stableReported = false;

            result.WindowSeconds = totalWall;
            result.WindowProgressSeconds = totalProgress;
            result.Reason = stalledSeconds >= 6 ? "SevereStall" :
                totalWall >= 16 && totalProgress / totalWall < 0.85 && totalWall - totalProgress >= 3
                    ? "SlowProgress" : "Normal";
            result.NeedsRecovery = result.Reason != "Normal";
            if (normalSeconds >= 30 && !stableReported)
            {
                result.Stable = true;
                stableReported = true;
            }
            return result;
        }
    }

    public sealed class HlsRecoveryBudget
    {
        public const int Limit = 2;
        public int Used { get; private set; }
        private double lastRecoverySeconds = double.NegativeInfinity;

        public bool TryUse(double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                Used >= Limit || elapsedSeconds - lastRecoverySeconds < 60) return false;
            Used++;
            lastRecoverySeconds = elapsedSeconds;
            return true;
        }
    }

    public static class HlsCdnIdentity
    {
        public static string GetHost(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                (uri.Scheme == "https" || uri.Scheme == "http") &&
                uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                ? uri.IdnHost.ToLowerInvariant() : null;
        }

        public static string GetFamily(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            if (!host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase)) return host;
            var label = host.Split('.')[0];
            var gotcha = label.IndexOf("gotcha", StringComparison.Ordinal);
            if (gotcha >= 0)
            {
                var end = gotcha + 6;
                while (end < label.Length && char.IsDigit(label[end])) end++;
                if (end > gotcha + 6) return label.Substring(gotcha, end - gotcha) + ".bilivideo.com";
            }
            var parts = label.Split('-');
            return parts.Length >= 3 && parts[0] == "cn"
                ? string.Join("-", parts.Take(3)) + ".bilivideo.com" : host;
        }

        public static int PreferenceRank(string url, string preferredHost)
        {
            var host = GetHost(url);
            if (host == null || string.IsNullOrEmpty(preferredHost)) return 2;
            if (host == preferredHost) return 0;
            return GetFamily(host) == GetFamily(preferredHost) ? 1 : 2;
        }
    }
}
