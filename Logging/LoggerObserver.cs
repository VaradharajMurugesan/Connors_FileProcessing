using NLog;
using NLog.Config;
using NLog.Targets;
using System;

namespace ProcessFiles_Demo.Logging
{
    public class LoggerObserver
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        static LoggerObserver()
        {
            // Load the NLog configuration
            var config = LogManager.Configuration;

            // Get the file target from the config
            var fileTarget = config.FindTargetByName<FileTarget>("logfile");
            string processor_type = AppDomain.CurrentDomain.GetData("ProcessorType")?.ToString() ?? "default"; // or another way to pass processor type

            // Set dynamic file name and archive file name
            fileTarget.FileName = $"logs/{processor_type}_log_{DateTime.Now:yyyyMMdd}.log"; // Dynamic log file name
            fileTarget.ArchiveFileName = $"logs/archive/{processor_type}_log_{DateTime.Now:yyyyMMdd_HHmmss}.log"; // Dynamic archive file name

            // Apply the modified configuration
            LogManager.Configuration = config;
        }

        public static void Debug(string message)
        {
            logger.Debug(message);
        }

        public static void Info(string message)
        {
            logger.Info(message);
        }

        public static void Error(Exception ex, string message)
        {
            logger.Error(ex, message);
        }

        public static void OnFileFailed(string message)
        {
            Error(new Exception(message), message);
        }

        public static void LogFileProcessed(string message)
        {
            Info(message);
        }

        // Add more logging methods as needed (e.g., Warn, Fatal, etc.)
    }
}
