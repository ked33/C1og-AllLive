using System;
using System.Threading;
using System.Threading.Tasks;
using AllLive.Core.Helper;

namespace AllLive.Core
{
    /// <summary>抖音风控拦截类型。</summary>
    public enum DouyinBlockKind
    {
        /// <summary>HTTP 444 风控拦截。</summary>
        Http444,
        /// <summary>验证码中间页（HTTP 200 但返回验证码页面）。</summary>
        Captcha
    }

    /// <summary>
    /// 抖音请求闸门：任一抖音直播间数据请求命中风控（HTTP 444 或验证码中间页）后，
    /// 暂停所有抖音直播间数据请求若干秒（默认 30 秒，可由宿主配置），
    /// 暂停期间 WaitAsync 会等待，暂停结束后自动放行。
    /// </summary>
    public static class DouyinRequestGate
    {
        private const int DefaultPauseSeconds = 30;
        private const int MinPauseSeconds = 1;
        private const int MaxPauseSeconds = 600;
        private const int MaxWaitSliceMilliseconds = 500;
        private const int ResumeJitterMilliseconds = 800;

        private static readonly object _randomSync = new object();
        private static readonly Random _random = new Random();
        private static long _pausedUntilTicks;

        /// <summary>命中 HTTP 444 时的暂停秒数提供者（默认 30 秒），由宿主设置。</summary>
        public static Func<int> BlockedPauseSecondsProvider { get; set; }

        /// <summary>命中验证码中间页时的暂停秒数提供者（默认 30 秒），由宿主设置。</summary>
        public static Func<int> CaptchaPauseSecondsProvider { get; set; }

        /// <summary>暂停开始时触发；暂停被延长时会再次触发。</summary>
        public static event EventHandler Paused;

        /// <summary>当前是否处于风控暂停期。</summary>
        public static bool IsPaused
        {
            get { return PauseRemainingSeconds > 0; }
        }

        /// <summary>暂停剩余秒数（向上取整，未暂停时为 0）。</summary>
        public static int PauseRemainingSeconds
        {
            get
            {
                var remainingTicks = Volatile.Read(ref _pausedUntilTicks) - DateTimeOffset.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    return 0;
                }
                return (int)Math.Ceiling(remainingTicks / (double)TimeSpan.TicksPerSecond);
            }
        }

        /// <summary>记录一次风控命中并暂停后续请求（只会延长、不会缩短已生效的暂停）。</summary>
        public static void ReportBlocked(DouyinBlockKind kind, string reason)
        {
            var seconds = GetPauseSeconds(kind);
            var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (true)
            {
                var current = Volatile.Read(ref _pausedUntilTicks);
                if (until.Ticks <= current)
                {
                    break;
                }
                if (Interlocked.CompareExchange(ref _pausedUntilTicks, until.Ticks, current) == current)
                {
                    break;
                }
            }

            CoreDebug.Log(() => $"[Douyin] 命中风控(kind={(kind == DouyinBlockKind.Captcha ? "Captcha" : "Http444")}) 暂停抖音请求 {seconds}s reason={reason} until={until:HH:mm:ss}");

            var handler = Paused;
            if (handler != null)
            {
                try
                {
                    handler(null, EventArgs.Empty);
                }
                catch
                {
                }
            }
        }

        /// <summary>等待风控暂停结束（未暂停时立即返回）。</summary>
        public static async Task WaitAsync()
        {
            var waited = false;
            while (true)
            {
                var remainingTicks = Volatile.Read(ref _pausedUntilTicks) - DateTimeOffset.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    break;
                }
                if (!waited)
                {
                    waited = true;
                    var waitSeconds = (int)Math.Ceiling(remainingTicks / (double)TimeSpan.TicksPerSecond);
                    CoreDebug.Log(() => $"[Douyin] 风控暂停中，请求等待 {waitSeconds}s 后继续");
                }
                var sliceTicks = Math.Min(remainingTicks, MaxWaitSliceMilliseconds * TimeSpan.TicksPerMillisecond);
                var delayMilliseconds = (int)Math.Max(1L, sliceTicks / TimeSpan.TicksPerMillisecond);
                await Task.Delay(delayMilliseconds).ConfigureAwait(false);
            }
            if (waited)
            {
                // 暂停结束后加一点抖动，避免并发请求在同一瞬间集中恢复再次触发风控。
                await Task.Delay(GetResumeJitterMilliseconds()).ConfigureAwait(false);
            }
        }

        private static int GetPauseSeconds(DouyinBlockKind kind)
        {
            var provider = kind == DouyinBlockKind.Captcha ? CaptchaPauseSecondsProvider : BlockedPauseSecondsProvider;
            var seconds = DefaultPauseSeconds;
            if (provider != null)
            {
                try
                {
                    seconds = provider();
                }
                catch
                {
                    seconds = DefaultPauseSeconds;
                }
            }
            if (seconds < MinPauseSeconds)
            {
                return MinPauseSeconds;
            }
            if (seconds > MaxPauseSeconds)
            {
                return MaxPauseSeconds;
            }
            return seconds;
        }

        private static int GetResumeJitterMilliseconds()
        {
            lock (_randomSync)
            {
                return _random.Next(0, ResumeJitterMilliseconds + 1);
            }
        }
    }
}
