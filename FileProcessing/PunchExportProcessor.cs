using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ProcessFiles_Demo.Logging;
using OfficeOpenXml;


namespace ProcessFiles_Demo.FileProcessing
{
    
    public class PunchExportProcessor : ICsvFileProcessorStrategy
    {        
        private Dictionary<int, string> timeZoneMap;
        private Dictionary<string, TimeZoneInfo> timeZoneCache;
        private Dictionary<string, string> employeeLocationMap;
        public PunchExportProcessor()
        {
            // Load Time Zone mappings from JSON file
            string json = File.ReadAllText("timezones.json");
            timeZoneMap = JsonSerializer.Deserialize<Dictionary<int, string>>(json);

            // Initialize cache for TimeZoneInfo objects
            timeZoneCache = new Dictionary<string, TimeZoneInfo>();

            // Load employee-location mapping
            employeeLocationMap = LoadEmployeeLocationMap("2024.09.26 Employee_Location mapping.xlsx");
        }

        private Dictionary<string, string> LoadEmployeeLocationMap(string filePath)
        {
            // Set the LicenseContext for EPPlus
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var map = new Dictionary<string, string>();

            // Load the Excel package
            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                // Get the first worksheet in the file
                ExcelWorksheet worksheet = package.Workbook.Worksheets[0];

                int rowCount = worksheet.Dimension.Rows;
                int colEmployeeId = 1;   // Assuming Employee Number is in column 1
                int colLocation = 2;     // Assuming Labor Level 2 (Location) is in column 2

                // Loop through rows and read Employee Number and Location
                for (int row = 2; row <= rowCount; row++) // Starting from row 2, skipping header
                {
                    string employeeId = worksheet.Cells[row, colEmployeeId].Text.Trim();
                    string location = worksheet.Cells[row, colLocation].Text.Trim();

                    if (!string.IsNullOrWhiteSpace(employeeId) && !string.IsNullOrWhiteSpace(location))
                    {
                        map[employeeId] = location;
                    }
                }
            }

            return map;
        }

        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Start processing Punch Export CSV: {filePath} at {startTime}");

            const int batchSize = 1000;
            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"Processed_{destinationFileName}");

            var lineBuffer = new List<string>(batchSize);

            // Dictionary to hold grouped lines by time zone
            var groupedLines = new Dictionary<string, List<string>>();

            // Asynchronously read lines and group them by Time Zone ID
            using (var reader = new StreamReader(filePath))
            {
                // Read and skip the header line
                string headerLine = await reader.ReadLineAsync().ConfigureAwait(false);

                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var columns = line.Split(',');
                    if (columns.Length > 6)
                    {
                        string timeZoneId = columns[6].Trim(); // Trim to remove any leading/trailing spaces
                        if (!groupedLines.ContainsKey(timeZoneId))
                        {
                            groupedLines[timeZoneId] = new List<string>();
                        }
                        groupedLines[timeZoneId].Add(line);
                    }
                    else
                    {
                        LoggerObserver.OnFileFailed($"Malformed line: {line}");
                    }
                }
            }

            using (var writer = new StreamWriter(destinationFilePath))
            {
                // Write header to destination file
                await writer.WriteLineAsync("Employee ID,Location ID,Clock In Time,Clock In Type,Deleted,External ID,Role").ConfigureAwait(false);

                int index = 0;

                // Process each group of lines grouped by Time Zone ID
                foreach (var group in groupedLines)
                {
                    // Parse Time Zone ID to integer
                    if (!int.TryParse(group.Key, out int timeZoneId))
                    {
                        LoggerObserver.OnFileFailed($"Invalid TimeZoneID: {group.Key}");
                        continue; // Skip this group if Time Zone ID is invalid
                    }

                    TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZoneId);
                    if (timeZoneInfo == null)
                    {
                        // Skip processing for invalid or unfound time zones
                        continue;
                    }

                    // Parallel processing of each line within the same time zone
                    var tasks = group.Value.Select(line => ProcessLineWithTimeZoneAsync(line, timeZoneInfo));

                    // Await all processing tasks for the current group
                    var processedLines = await Task.WhenAll(tasks).ConfigureAwait(false);

                    foreach (var processedLine in processedLines)
                    {
                        if (processedLine != null)
                        {
                            lineBuffer.Add(processedLine);
                        }

                        if (++index % batchSize == 0)
                        {
                            await WriteBatchAsync(writer, lineBuffer).ConfigureAwait(false);
                            lineBuffer.Clear(); // Clear the buffer after writing
                        }
                    }
                }

                // Write any remaining lines in the buffer
                if (lineBuffer.Any())
                {
                    await WriteBatchAsync(writer, lineBuffer).ConfigureAwait(false);
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

            // Fetch location from employee-location mapping
            string location = employeeLocationMap.ContainsKey(employeeId) ? employeeLocationMap[employeeId] : "Unknown";

            // Define the possible formats with both yyyy and yy
            string[] formats = { "M/d/yyyy H:mm", "M/d/yy H:mm", "M/d/yyyy h:mm tt", "M/d/yy h:mm tt" };

            // Parse date and time using the possible formats
            if (!DateTime.TryParseExact(dateTimeStr, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTime))
            {
                LoggerObserver.OnFileFailed($"Invalid DateTime format: {dateTimeStr}");
                dateTime = DateTime.MinValue; // Assign a default DateTime value
            }

            // Adjust the date/time according to the time zone
            DateTime adjustedDateTime = ConvertToTimeZone(dateTime, timeZoneInfo);

            // Format the ClockInTime as Kronos format
            string clockInTime = adjustedDateTime.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

            // Generate ClockInType based on the Punch Type
            string clockInType = GetClockInType(punchType);

            //// External ID is a combination of Employee ID, Location, Date/time, and Labor Level Transfer
            string externalId = $"{employeeId}-{location}-{dateTimeStr}-{laborLevelTransfer}";

            // Return the formatted line to be written
            return $"{employeeId},{location},{clockInTime},{clockInType},,{externalId},";
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
