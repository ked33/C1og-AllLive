using NLog;
using NLog.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace AllLive.UWP.Helper
{
    public enum LogType
    {
        INFO,
        DEBUG,
        ERROR,
        FATAL
    }
    public class LogHelper
    {
        private static volatile bool enabled;
        private static readonly object configurationLock = new object();
        public static bool Enabled => enabled;
        public static volatile LoggingConfiguration config;
        public static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        public static void SetEnabled(bool enabled)
        {
            LogHelper.enabled = enabled;
            LocalServiceManager.UpdateLogging();
        }
        public static void LogDebug(string message)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Log(message, LogType.DEBUG);
        }

        public static void LogDebug(Func<string> messageFactory)
        {
            if (!Enabled || messageFactory == null)
            {
                return;
            }

            string message;
            try { message = messageFactory(); }
            catch { return; } // Diagnostics must never interrupt playback/cleanup.
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Log(message, LogType.DEBUG);
        }

        public static void Log(string message, LogType type, Exception ex = null)
        {
            if (!Enabled)
            {
                return;
            }
            try
            {
                if (config == null)
                {
                    lock (configurationLock)
                    {
                        if (config == null)
                        {
                            var logConfig = new LoggingConfiguration();
                            var logDirectory = Windows.Storage.ApplicationData.Current.LocalFolder.Path + @"\log\";
                            var logfile = new NLog.Targets.FileTarget()
                            {
                                Name = "logfile",
                                CreateDirs = true,
                                FileName = logDirectory + "AllLive.log",
                                ArchiveFileName = logDirectory + "AllLive.{#}.log",
                                ArchiveAboveSize = 5 * 1024 * 1024,
                                ArchiveEvery = NLog.Targets.FileArchivePeriod.Day,
                                ArchiveNumbering = NLog.Targets.ArchiveNumberingMode.Rolling,
                                MaxArchiveFiles = 5,
                                Layout = "${longdate}|${level:uppercase=true}|${logger}|${threadid}|${message}|${exception:format=Message,StackTrace}"
                            };
                            logConfig.AddRule(LogLevel.Debug, LogLevel.Fatal, logfile);
                            NLog.LogManager.Configuration = logConfig;
                            config = logConfig;
                        }
                    }
                }
                if (!Enabled) return;
                Debug.WriteLine("[" + LogType.INFO.ToString() + "]" + message);
                switch (type)
                {
                    case LogType.INFO:
                        logger.Info(message);
                        break;
                    case LogType.DEBUG:
                        logger.Debug(message);
                        break;
                    case LogType.ERROR:
                        logger.Error(ex, message);
                        break;
                    case LogType.FATAL:
                        logger.Fatal(ex, message);
                        break;
                    default:
                        break;
                }
            }
            catch
            {
                // File/configuration failures must not affect recovery resource ownership.
            }
        }
        public static bool IsNetworkError(Exception ex)
        {
            if (ex.HResult == -2147012867 || ex.HResult == -2147012889)
            {
                return true;
            }
            {
                return false;
            }
        }

    }
}
