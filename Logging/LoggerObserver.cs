using System;
using System.IO;

namespace ProcessFiles_Demo.Logging
{
    public static class LoggerObserver
    {
        private static readonly string LogFilePath = "application.log"; // Specify the log file path

        // Method to log file processing information
        public static void LogFileProcessed(string filePath)
        {
            LogToFile($"INFO: File processed: {filePath}");
        }

        // Method to log file processing failures
        public static void OnFileFailed(string filePath)
        {
            LogToFile($"ERROR: File failed: {filePath}");
        }


        private static readonly object logLock = new object();
        // Private method to handle actual file logging
        private static void LogToFile(string message)
        {
            try
            {
                lock (logLock) // Ensures only one thread can access the file at a time
                {
                    // Append log message to the file
                    using (var writer = new StreamWriter(LogFilePath, append: true))
                    {
                        writer.WriteLine($"{DateTime.Now}: {message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle any exceptions that occur during logging
                Console.WriteLine($"Logging error: {ex.Message}");
            }
        }
    }
}
