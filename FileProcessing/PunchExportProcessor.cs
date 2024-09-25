using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessFiles_Demo.Logging;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PunchExportProcessor : ICsvFileProcessorStrategy
    {
        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            // Log start time
            DateTime startTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Start processing Punch Export CSV: {filePath} at {startTime}");

            const int batchSize = 1000;
            string destinationFileName = Path.GetFileName(filePath);
            var lines = File.ReadLines(filePath).Skip(1); // Skip header
            var destinationFilePath = Path.Combine(destinationPath, $"Processed_{destinationFileName}");

            var lineBuffer = new List<string>(batchSize);

            using (var writer = new StreamWriter(destinationFilePath))
            {
                // Write header to destination file
                writer.WriteLine("Employee ID,Location ID,Clock In Time,Clock In Type,Deleted,External ID,Role");

                int index = 0;
                foreach (var line in lines)
                {
                    var processedLine = await ProcessLineAsync(line);
                    if (processedLine != null)
                    {
                        lineBuffer.Add(processedLine);
                    }

                    // Write to file when buffer is full
                    if (++index % batchSize == 0)
                    {
                        await WriteBatchAsync(writer, lineBuffer);
                        lineBuffer.Clear(); // Clear the buffer after writing
                    }
                }

                // Write any remaining lines in the buffer
                if (lineBuffer.Any())
                {
                    await WriteBatchAsync(writer, lineBuffer);
                }
            }

            // Log end time
            DateTime endTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Finished processing Punch Export CSV: {filePath} at {endTime}");

            // Calculate and log the total time taken
            TimeSpan duration = endTime - startTime;
            LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
        }

        // Process individual lines and return the result for writing
        private async Task<string> ProcessLineAsync(string line)
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
            DateTime dateTime;
            if (!DateTime.TryParseExact(dateTimeStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
            {
                LoggerObserver.OnFileFailed($"Invalid DateTime format: {dateTimeStr}");
                return null;
            }

            // Format the ClockInTime as Kronos format
            string clockInTime = dateTime.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

            // Generate ClockInType based on the Punch Type
            string clockInType = GetClockInType(punchType);

            // External ID is a combination of Employee ID, Date/time, and Labor Level Transfer
            string externalId = $"{employeeId}-{dateTimeStr}-{laborLevelTransfer}";

            // Return the formatted line to be written
            return $"{employeeId},{laborLevelTransfer},{clockInTime},{clockInType},,{externalId},";
        }

        // Buffer writing method to write a batch of lines at once
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
                    // Return the value as it is for these punch types
                    return punchType;

                default:
                    return "Unknown Punch Type";
            }
        }
    }
}
