using System;
using NLog;

namespace ProcessFiles_Demo.Logging
{
    public static class LoggerObserver
    {
        // Lazy initialization of the Logger
        private static Logger Logger => LogManager.GetCurrentClassLogger();

        // Method to log file processing information
        public static void LogFileProcessed(string filePath)
        {
            Logger.Info($"File processed: {filePath}");
        }

        // Method to log file processing failures
        public static void OnFileFailed(string message)
        {
            Logger.Error(message);
        }

        // Optional: Method to log exceptions with stack trace
        public static void LogException(Exception ex, string contextMessage = null)
        {
            if (contextMessage != null)
            {
                Logger.Error(ex, contextMessage);
            }
            else
            {
                Logger.Error(ex);
            }
        }
    }
}
