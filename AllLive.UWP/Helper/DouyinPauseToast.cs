using AllLive.Core;
using AllLive.UWP.Controls;
using System;
using System.Collections.Concurrent;
using System.Threading;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace AllLive.UWP.Helper
{
    /// <summary>
    /// 抖音风控暂停倒计时 Toast：命中 444/验证码中间页后，在每个已注册窗口常驻显示剩余秒数，
    /// 暂停结束自动关闭。
    /// </summary>
    internal static class DouyinPauseToast
    {
        private static readonly ConcurrentDictionary<CoreDispatcher, Presenter> Presenters = new ConcurrentDictionary<CoreDispatcher, Presenter>();
        private static int _initialized;

        /// <summary>订阅风控闸门事件（幂等），在 App 构造函数中调用。</summary>
        public static void Initialize()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
            {
                return;
            }
            DouyinRequestGate.Paused += OnPaused;
        }

        /// <summary>注册当前视图：主窗口与每个直播窗口在创建时各调用一次。</summary>
        public static void RegisterCurrentView()
        {
            CoreDispatcher dispatcher;
            try
            {
                dispatcher = CoreApplication.GetCurrentView()?.Dispatcher;
            }
            catch
            {
                return;
            }
            if (dispatcher == null)
            {
                return;
            }
            Presenters.TryAdd(dispatcher, new Presenter());
        }

        private static void OnPaused(object sender, EventArgs e)
        {
            foreach (var pair in Presenters)
            {
                try
                {
                    if (pair.Key.TryRunAsync(CoreDispatcherPriority.Normal, pair.Value.Refresh) == null)
                    {
                        RemovePresenter(pair.Key);
                    }
                }
                catch
                {
                    // 调度失败说明窗口已关闭，移出注册表。
                    RemovePresenter(pair.Key);
                }
            }
        }

        private static void RemovePresenter(CoreDispatcher dispatcher)
        {
            Presenter ignored;
            Presenters.TryRemove(dispatcher, out ignored);
        }

        // 每个窗口一个倒计时表现器：1 秒一跳，剩余秒数直接读闸门状态（暂停延长时自动正确），归零关闭。
        private sealed class Presenter
        {
            private MessageToast _toast;
            private DispatcherTimer _timer;

            public void Refresh()
            {
                try
                {
                    if (_toast == null || _toast.IsClosed)
                    {
                        _toast = new MessageToast(BuildText(), true);
                        _toast.Show();
                    }
                    else
                    {
                        _toast.SetText(BuildText());
                    }
                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                        _timer.Tick += Timer_Tick;
                    }
                    if (!_timer.IsEnabled)
                    {
                        _timer.Start();
                    }
                }
                catch
                {
                    _toast = null;
                }
            }

            private void Timer_Tick(object sender, object e)
            {
                try
                {
                    if (DouyinRequestGate.IsPaused)
                    {
                        if (_toast != null && !_toast.IsClosed)
                        {
                            _toast.SetText(BuildText());
                        }
                        return;
                    }
                }
                catch
                {
                }
                Stop();
            }

            private void Stop()
            {
                try
                {
                    if (_timer != null)
                    {
                        _timer.Stop();
                    }
                }
                catch
                {
                }
                try
                {
                    if (_toast != null)
                    {
                        _toast.Close();
                    }
                }
                catch
                {
                }
                _toast = null;
            }

            private static string BuildText()
            {
                return $"抖音请求已暂停，{DouyinRequestGate.PauseRemainingSeconds} 秒后恢复";
            }
        }
    }
}
