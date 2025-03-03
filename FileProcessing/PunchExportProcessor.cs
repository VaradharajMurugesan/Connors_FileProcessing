using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FileTransform.Logging;
using OfficeOpenXml;
using FileTransform.SFTPExtract;
using FileTransform.DataModel;
using Newtonsoft.Json.Linq;

namespace FileTransform.FileProcessing
{
    public class PunchExportProcessor : ICsvFileProcessorStrategy
    {
        private Dictionary<int, string> timeZoneMap;
        private Dictionary<string, TimeZoneInfo> timeZoneCache;
        private Dictionary<string, EmployeeHrData> employeeHrMapping;
        ExtractEmployeeEntityData extractEmployeeEntityData = new ExtractEmployeeEntityData();
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();

        public PunchExportProcessor(JObject clientSettings)
        {
            // Load Time Zone mappings from JSON file
            string json = File.ReadAllText("timezones.json");
            timeZoneMap = JsonSerializer.Deserialize<Dictionary<int, string>>(json);

            // Initialize cache for TimeZoneInfo objects
            timeZoneCache = new Dictionary<string, TimeZoneInfo>();

            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            string remoteMappingFilePath = "/home/fivebelow-uat/outbox/extracts";
            string employeeEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath, "EmployeeEntity");

            // Load employee HR mapping from CSV
            employeeHrMapping = extractEmployeeEntityData.LoadGroupedEmployeeHrMappingFromCsv(employeeEntityMappingPath);
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
            var groupedRecords = new Dictionary<string, List<string>>();

            // Read and group records
            using (var reader = new StreamReader(filePath))
            {
                string headerLine = await reader.ReadLineAsync().ConfigureAwait(false); // Read header

                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var columns = line.Split(',');
                    if (columns.Length >= 5)
                    {
                        string employeeId = columns[0].Trim();
                        string timeZoneId = columns[6].Trim();
                        string key = $"{timeZoneId}-{employeeId}";

                        if (!groupedRecords.ContainsKey(key))
                        {
                            groupedRecords[key] = new List<string>();
                        }

                        groupedRecords[key].Add(line);
                    }
                    else
                    {
                        LoggerObserver.OnFileFailed($"Malformed line: {line}");
                    }
                }
            }

            using (var writer = new StreamWriter(destinationFilePath))
            {
                await writer.WriteLineAsync("employeeid,locationId,clockintime,clockintype,deleted,externalId,role").ConfigureAwait(false);
                // Dictionary to maintain the last punch type for each employee
                var lastPunchTypes = new Dictionary<string, string>();
                foreach (var group in groupedRecords)
                {
                    string timeZoneIdStr = group.Key.Split('-')[0];

                    if (!int.TryParse(timeZoneIdStr, out int timeZoneId))
                    {
                        LoggerObserver.OnFileFailed($"Invalid TimeZoneID: {timeZoneIdStr}");
                        continue;
                    }

                    TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZoneId);
                    if (timeZoneInfo == null)
                        continue;

                    var sortedRecords = group.Value
                        .OrderBy(line =>
                        {
                            var cols = line.Split(',');
                            return DateTime.TryParse(cols[1], out var dt) ? dt : DateTime.MinValue;
                        })
                        .ToList();

                    List<List<string>> groupedBy24Hours = GroupRecordsBy24Hours(sortedRecords, timeZoneInfo);

                    foreach (var recordGroup in groupedBy24Hours)
                    {

                        for (int i = 0; i < recordGroup.Count; i++)
                        {
                            string currentLine = recordGroup[i];
                            var processedLine = await ProcessLineWithTimeZoneAsync(currentLine, timeZoneInfo, i < recordGroup.Count - 1 ? recordGroup[i + 1] : null, lastPunchTypes);
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
            LoggerObserver.LogFileProcessed($"Time taken: {duration.TotalSeconds} seconds.");
        }

        private List<List<string>> GroupRecordsBy24Hours(List<string> records, TimeZoneInfo timeZoneInfo)
        {
            var groupedRecords = new List<List<string>>();
            List<string> currentGroup = new List<string>();
            DateTime? firstRecordTimeInGroup = null;

            foreach (var record in records)
            {
                var cols = record.Split(',');
                if (!DateTime.TryParse(cols[1], out DateTime recordTime))
                    continue;

                DateTime adjustedTime = ConvertToTimeZone(recordTime, timeZoneInfo);

                // Check the punch type (assuming it's in the 3rd column)
                string punchType = cols[5].ToLower(); // Convert punchType to lowercase for comparison

                // If punchType is "out", split the group irrespective of the 24 hours gap
                if (punchType == "new shift" && currentGroup.Any())
                {
                    groupedRecords.Add(new List<string>(currentGroup));
                    currentGroup.Clear();
                    firstRecordTimeInGroup = adjustedTime; // Reassign firstRecordTimeInGroup to the first record of the new group
                }

                // If it's the first record or within 24 hours of the first record, add it to the current group
                if (!firstRecordTimeInGroup.HasValue || (adjustedTime - firstRecordTimeInGroup.Value).TotalHours <= 16)
                {
                    currentGroup.Add(record);
                }
                else
                {
                    // If 24 hours have passed, finalize the current group and start a new one
                    groupedRecords.Add(new List<string>(currentGroup));
                    currentGroup.Clear();
                    currentGroup.Add(record); // First record of the new group

                    // Reassign firstRecordTimeInGroup to the first record of the new group
                    firstRecordTimeInGroup = adjustedTime;
                }

                // If it's the first record in the group, set the firstRecordTimeInGroup
                if (!firstRecordTimeInGroup.HasValue)
                {
                    firstRecordTimeInGroup = adjustedTime;
                }
            }

            // Add any remaining records in the current group
            if (currentGroup.Any())
            {
                groupedRecords.Add(currentGroup);
            }

            return groupedRecords;
        }




        // Process individual lines with time zone adjustment
        private async Task<string> ProcessLineWithTimeZoneAsync(string line, TimeZoneInfo timeZoneInfo, string nextLine, Dictionary<string, string> lastPunchTypes)
        {
            var columns = line.Split(',');
            if (columns.Length < 6) // Ensure the line has enough columns
            {
                LoggerObserver.OnFileFailed($"Malformed line: {line}");
                return null;
            }

            // Extract relevant fields
            string employeeId = columns[0];
            string dateTimeStr = columns[1];
            string punchType = columns[5]; // Column index for Punch Type

            // Fetch location from employee-location mapping
            //string location = employeeLocationMap.ContainsKey(employeeId) ? employeeLocationMap[employeeId] : "Unknown";
            // Fetch locationId from employeeHrMapping, default to "Unknown" if not found
            string location = employeeHrMapping.TryGetValue(employeeId, out var hrData)
                ? hrData?.LocationExternalId ?? "Unknown"
                : "Unknown";

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
            string externalId = $"{employeeId}-{location}-{dateTimeStr}".Replace(" ", "").Replace("/", "-");

            // Store the current punch type as the last punch type for this employee
            lastPunchTypes[employeeId] = punchType;

            // Return the formatted line to be written
            return $"{employeeId},{location},{clockInTime},{clockInType},,{externalId},crew";
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

        private DateTime ConvertToTimeZone(DateTime dateTime, TimeZoneInfo timeZone)
        {
            return TimeZoneInfo.ConvertTimeToUtc(dateTime, timeZone);
        }
        // Method to map punch type to ClockInType with conditions
        // Updated method to determine ClockInType with last punch type tracking
        private string GetClockInType(string punchType, string nextPunchType, Dictionary<string, string> lastPunchTypes, string employeeId)
        {
            // Check for last punch type
            string lastPunchType = lastPunchTypes.ContainsKey(employeeId) ? lastPunchTypes[employeeId] : null;

            // Handle ClockInType based on the current and last punch types
            if (punchType == "Out Punch" & (nextPunchType == null || lastPunchType == null))
            {
                return "ShiftEnd"; // Default for "Out Punch"
            }

            else if (punchType == "Out Punch")
            {
                if (IsMealBreakType(nextPunchType) || lastPunchType == "New Shift")
                {
                    return "MealBreakBegin";
                }
                //return "ShiftEnd"; // Default for "Out Punch"
            }

            else if (punchType == "New Shift")
            {
                return "ShiftBegin";
            }

            else if (IsMealBreakEndType(punchType))
            {
                return "MealBreakEnd";
            }

            // Handle rest break types
            else if (lastPunchType == "RestBreakBegin" && punchType == "RestBreakEnd")
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
                   punchType == "CA 30 Min Meal at GT 5 Hrs" ||
                   punchType == "CA 2nd 30 Min Meal by 10 Hrs";
        }

        // Helper method to check if the punch type is a meal break end type
        private bool IsMealBreakEndType(string punchType)
        {
            return punchType == "30 Min Meal" ||
                   punchType == "CA 30 Min Meal at LT 5 Hrs" ||
                   punchType == "CA Less Than a 30 Minute Meal" ||
                   punchType == "CA 30 Min Meal at GT 5 Hrs" ||
                   punchType == "CA 2nd 30 Min Meal by 10 Hrs"; 
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
