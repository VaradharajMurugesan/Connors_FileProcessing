using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProcessFiles_Demo.Logging;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PunchExportProcessor : ICsvFileProcessorStrategy
    {
        private Dictionary<int, string> timeZoneMap;
        private Dictionary<string, TimeZoneInfo> timeZoneCache;

        public PunchExportProcessor()
        {
            // Load Time Zone mappings from JSON file
            string json = File.ReadAllText("timezones.json");
            timeZoneMap = JsonSerializer.Deserialize<Dictionary<int, string>>(json);

            // Initialize cache for TimeZoneInfo objects
            timeZoneCache = new Dictionary<string, TimeZoneInfo>();
        }

        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Start processing Punch Export CSV: {filePath} at {startTime}");

            const int batchSize = 1000;
            string destinationFileName = Path.GetFileName(filePath);
            var lines = await File.ReadAllLinesAsync(filePath).ConfigureAwait(false); // Read lines asynchronously
            var destinationFilePath = Path.Combine(destinationPath, $"Processed_{destinationFileName}");

            var lineBuffer = new List<string>(batchSize);

            using (var writer = new StreamWriter(destinationFilePath))
            {
                // Write header to destination file
                writer.WriteLine("Employee ID,Location ID,Clock In Time,Clock In Type,Deleted,External ID,Role");

                int index = 0;

                // Group lines by time zone ID before processing
                var groupedByTimeZone = lines.Skip(1) // Skip header
                                             .GroupBy(line => line.Split(',')[6]); // Assuming 6th column is the Time Zone ID

                foreach (var group in groupedByTimeZone)
                {
                    int timeZoneId = int.Parse(group.Key);
                    TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZoneId);

                    // Parallel processing of each group of lines with the same time zone
                    var tasks = group.Select(line => Task.Run(async () =>
                    {
                        var processedLine = await ProcessLineWithTimeZoneAsync(line, timeZoneInfo);
                        return processedLine;
                    }));

                    var processedLines = await Task.WhenAll(tasks);

                    foreach (var processedLine in processedLines)
                    {
                        if (processedLine != null)
                        {
                            lineBuffer.Add(processedLine);
                        }

                        if (++index % batchSize == 0)
                        {
                            await WriteBatchAsync(writer, lineBuffer);
                            lineBuffer.Clear(); // Clear the buffer after writing
                        }
                    }
                }

                // Write any remaining lines in the buffer
                if (lineBuffer.Any())
                {
                    await WriteBatchAsync(writer, lineBuffer);
                }
            }

            DateTime endTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Finished processing Punch Export CSV: {filePath} at {endTime}");
            TimeSpan duration = endTime - startTime;
            LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
        }

        // Process individual lines with time zone adjustment
        private async Task<string> ProcessLineWithTimeZoneAsync(string line, TimeZoneInfo timeZoneInfo)
        {
            var columns = line.Split(',');
            if (columns.Length < 8) // Ensure the line has enough columns
            {
                LoggerObserver.OnFileFailed($"Malformed line: {line}");
                return null;
            }

            // Extract relevant fields
            string employeeId = columns[0];
            string dateTimeStr = columns[1];
            string laborLevelTransfer = columns[2];
            string punchType = columns[5]; // Column index for Punch Type

            // Define the possible formats with both yyyy and yy
            string[] formats = { "M/d/yyyy H:mm", "M/d/yy H:mm", "M/d/yyyy h:mm tt", "M/d/yy h:mm tt" };

            // Parse date and time using the possible formats
            if (!DateTime.TryParseExact(dateTimeStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTime))
            {
                LoggerObserver.OnFileFailed($"Invalid DateTime format: {dateTimeStr}");
                return null;
            }

            // Adjust the date/time according to the time zone
            DateTime adjustedDateTime = ConvertToTimeZone(dateTime, timeZoneInfo);

            // Format the ClockInTime as Kronos format
            string clockInTime = adjustedDateTime.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

            // Generate ClockInType based on the Punch Type
            string clockInType = GetClockInType(punchType);

            // External ID is a combination of Employee ID, Date/time, and Labor Level Transfer
            string externalId = $"{employeeId}-{dateTimeStr}-{laborLevelTransfer}";

            // Return the formatted line to be written
            return $"{employeeId},{laborLevelTransfer},{clockInTime},{clockInType},,{externalId},";
        }

        // Method to get TimeZoneInfo from cache or fetch if not present
        private TimeZoneInfo GetTimeZoneInfo(int timeZoneId)
        {
            if (!timeZoneMap.TryGetValue(timeZoneId, out string timeZoneIdName))
            {
                LoggerObserver.OnFileFailed($"Invalid TimeZoneID: {timeZoneId}");
                return null;
            }

            if (!timeZoneCache.ContainsKey(timeZoneIdName))
            {
                try
                {
                    // Cache the TimeZoneInfo object for future use
                    timeZoneCache[timeZoneIdName] = TimeZoneInfo.FindSystemTimeZoneById(timeZoneIdName);
                }
                catch (TimeZoneNotFoundException ex)
                {
                    LoggerObserver.OnFileFailed($"Time zone not found: {timeZoneIdName}. Error: {ex.Message}");
                    return null;
                }
            }

            return timeZoneCache[timeZoneIdName];
        }

        // Method to adjust DateTime based on Time Zone
        private DateTime ConvertToTimeZone(DateTime dateTime, TimeZoneInfo timeZoneInfo)
        {
            if (timeZoneInfo == null)
            {
                // Return the original dateTime or apply a default timezone (e.g., UTC) if no valid timezone is found
                return dateTime; // You can apply a default conversion here if needed
            }

            return TimeZoneInfo.ConvertTimeFromUtc(dateTime, timeZoneInfo);
        }

        // Buffer writing method to write a batch of lines at once asynchronously
        private async Task WriteBatchAsync(StreamWriter writer, List<string> lines)
        {
            foreach (var line in lines)
            {
                await writer.WriteLineAsync(line);
            }
        }

        // Method to map punch type to ClockInType
        private string GetClockInType(string punchType)
        {
            switch (punchType)
            {
                case "ShiftBegin":
                    return "New Shift";

                case "ShiftEnd":
                case "MealBreakBegin":
                    return "Out Punch";

                case "MealBreakEnd":
                    return "30 Min Meal";

                case "RestBreakBegin":
                case "RestBreakEnd":
                    return "N/A";

                case "New Shift":
                case "Out Punch":
                case "30 Min Meal":
                    return punchType;

                default:
                    return "Unknown Punch Type";
            }
        }
    }
}
