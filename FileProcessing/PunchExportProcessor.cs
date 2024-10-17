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
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Punch Export CSV: {filePath} at {startTime}");

            const int batchSize = 1000;
            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"Clockin_{timestamp}.csv");

            var lineBuffer = new List<string>(batchSize);
            var groupedLines = new Dictionary<string, Dictionary<string, List<string>>>(); // Group by Time Zone and Employee ID

            // Asynchronously read lines and group them by Time Zone ID and Employee ID
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
                        string timeZoneId = columns[6].Trim(); // Get Time Zone ID
                        string employeeId = columns[0].Trim(); // Get Employee ID

                        // Initialize group if not already present
                        if (!groupedLines.ContainsKey(timeZoneId))
                        {
                            groupedLines[timeZoneId] = new Dictionary<string, List<string>>();
                        }

                        if (!groupedLines[timeZoneId].ContainsKey(employeeId))
                        {
                            groupedLines[timeZoneId][employeeId] = new List<string>();
                        }

                        groupedLines[timeZoneId][employeeId].Add(line);
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
                await writer.WriteLineAsync("employeeid,locationId,clockintime,clockintype,deleted,externalId,role").ConfigureAwait(false);

                // Dictionary to maintain the last punch type for each employee
                var lastPunchTypes = new Dictionary<string, string>();

                // Process each group of lines grouped by Time Zone ID and Employee ID
                foreach (var timeZoneGroup in groupedLines)
                {
                    string timeZoneIdStr = timeZoneGroup.Key;

                    // Parse Time Zone ID to integer
                    if (!int.TryParse(timeZoneIdStr, out int timeZoneId))
                    {
                        LoggerObserver.OnFileFailed($"Invalid TimeZoneID: {timeZoneIdStr}");
                        continue; // Skip this group if Time Zone ID is invalid
                    }

                    TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZoneId);
                    if (timeZoneInfo == null)
                    {
                        continue; // Skip processing for invalid or unfound time zones
                    }

                    foreach (var employeeGroup in timeZoneGroup.Value)
                    {
                        // Sort the punches for this employee by check-in time
                        var sortedLines = employeeGroup.Value
                            .OrderBy(line =>
                            {
                                var columns = line.Split(',');
                                return DateTime.TryParse(columns[1], out var dateTime) ? dateTime : DateTime.MinValue;
                            })
                            .ToList();

                        // Process each sorted line
                        for (int i = 0; i < sortedLines.Count; i++)
                        {
                            string currentLine = sortedLines[i];
                            var processedLine = await ProcessLineWithTimeZoneAsync(currentLine, timeZoneInfo, i < sortedLines.Count - 1 ? sortedLines[i + 1] : null, lastPunchTypes);
                            if (processedLine != null)
                            {
                                lineBuffer.Add(processedLine);
                            }

                            if (lineBuffer.Count % batchSize == 0)
                            {
                                await WriteBatchAsync(writer, lineBuffer).ConfigureAwait(false);
                                lineBuffer.Clear(); // Clear the buffer after writing
                            }
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
        private async Task<string> ProcessLineWithTimeZoneAsync(string line, TimeZoneInfo timeZoneInfo, string nextLine, Dictionary<string, string> lastPunchTypes)
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

            // Prepare for next punch type lookup
            string nextPunchType = null;
            if (!string.IsNullOrEmpty(nextLine))
            {
                var nextColumns = nextLine.Split(',');
                if (nextColumns.Length > 5)
                {
                    nextPunchType = nextColumns[5]; // Get the punch type from the next line
                }
            }

            // Format the ClockInTime as Kronos format
            string clockInTime = adjustedDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            // Generate ClockInType based on the Punch Type and next Punch Type
            string clockInType = GetClockInType(punchType, nextPunchType, lastPunchTypes, employeeId);

            // External ID is a combination of Employee ID, Location, Date/time
            string externalId = $"{employeeId}-{location}-{dateTimeStr}";

            // Store the current punch type as the last punch type for this employee
            lastPunchTypes[employeeId] = punchType;

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

        // Method to convert DateTime to a specific time zone
        private DateTime ConvertToTimeZone(DateTime dateTime, TimeZoneInfo timeZone)
        {
            return TimeZoneInfo.ConvertTime(dateTime, timeZone);
        }

        // Method to map punch type to ClockInType with conditions
        // Updated method to determine ClockInType with last punch type tracking
        private string GetClockInType(string punchType, string nextPunchType, Dictionary<string, string> lastPunchTypes, string employeeId)
        {
            // Check for last punch type
            string lastPunchType = lastPunchTypes.ContainsKey(employeeId) ? lastPunchTypes[employeeId] : null;

            // Handle ClockInType based on the current and last punch types
            if (punchType == "Out Punch")
            {
                if (IsMealBreakType(nextPunchType) || lastPunchType == "New Shift")
                {
                    return "MealBreakBegin";
                }
                return "ShiftEnd"; // Default for "Out Punch"
            }

            if (punchType == "New Shift")
            {
                return "ShiftBegin";
            }

            if (IsMealBreakEndType(punchType))
            {
                return "MealBreakEnd";
            }

            // Handle rest break types
            if (lastPunchType == "RestBreakBegin" && punchType == "RestBreakEnd")
            {
                return "RestBreakEnd";
            }

            return punchType; // Return the punchType if no specific mapping found
        }

        // Helper method to check if the punch type is a meal break type
        private bool IsMealBreakType(string punchType)
        {
            return punchType == "30 Min Meal" ||
                   punchType == "CA 30 Min Meal at LT 5 Hrs" ||
                   punchType == "CA Less Than a 30 Minute Meal" ||
                   punchType == "CA 30 Min Meal at GT 5 Hrs";
        }

        // Helper method to check if the punch type is a meal break end type
        private bool IsMealBreakEndType(string punchType)
        {
            return punchType == "30 Min Meal" ||
                   punchType == "CA 30 Min Meal at LT 5 Hrs" ||
                   punchType == "CA Less Than a 30 Minute Meal" ||
                   punchType == "CA 30 Min Meal at GT 5 Hrs";
        }

        // Helper method to check if two DateTime values are on the same day
        private bool IsSameDay(DateTime dt1, DateTime dt2)
        {
            return dt1.Date == dt2.Date;
        }

        // Write a batch of lines to the file asynchronously
        private async Task WriteBatchAsync(StreamWriter writer, List<string> lines)
        {
            foreach (var line in lines)
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
    }
}
