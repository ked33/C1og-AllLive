using System;

namespace AllLive.Core.Helper
{
    public static class CoreDebug
    {
        public static Action<string> Logger { get; set; }
        public static Func<bool> IsEnabled { get; set; }

        public static bool Enabled
        {
            get
            {
                try
                {
                    return IsEnabled?.Invoke() ?? Logger != null;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Log(string message)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message))
            {
                return;
            }
            try
            {
                Logger?.Invoke(message);
            }
            catch
            {
            }
        }

        public static void Log(Func<string> messageFactory)
        {
            if (!Enabled || messageFactory == null)
            {
                return;
            }

            string message;
            try
            {
                message = messageFactory();
            }
            catch
            {
                return;
            }

            Log(message);
        }
    }
}
