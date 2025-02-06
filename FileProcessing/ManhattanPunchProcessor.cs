using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json.Linq;
using ProcessFiles_Demo.DataModel;
using ProcessFiles_Demo.Helpers;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.SFTPExtract;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace ProcessFiles_Demo.FileProcessing
{
    internal class ManhattanPunchProcessor : ICsvFileProcessorStrategy
    {
        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, ManhattanLocationData> LocationMapping;
        private Dictionary<string, LocationEntityData> TimeZoneMapping;
        private readonly HashSet<string> payrollProcessedFileNumbers;
        private bool mealBreakFlag = false;
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();
        ExtractLocationEntityData extractLocation = new ExtractLocationEntityData();

        public ManhattanPunchProcessor(JObject clientSettings)
        {
            var payroll_clientSettings = ClientSettingsLoader.LoadClientSettings("payroll");
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            mealBreakFlag = bool.TryParse(clientSettings["Flags"]["MealBrakRequired"]?.ToString(), out bool flag) && flag;
            LocationMapping = LoadLocationMapping("location mapping.csv");
            string remoteMappingFilePath = "/home/fivebelow-uat/outbox/extracts";
            string LocationEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath, "LocationEntity");
            TimeZoneMapping = extractLocation.LoadGroupedLocationMappingFromCsv(LocationEntityMappingPath);
        }

        public Dictionary<string, ManhattanLocationData> LoadLocationMapping(string filePath)
        {
            // Create a dictionary to hold the mappings by LocationExternalId
            var locationMappingDictionary = new Dictionary<string, ManhattanLocationData>();

            try
            {
                // Open the file using StreamReader and CsvHelper for efficient memory usage
                using (var reader = new StreamReader(filePath))
                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true, // Assuming the CSV has a header row
                    Delimiter = ",", // Ensure the delimiter is properly set
                }))
                {
                    // Read all records from the CSV file
                    var records = csv.GetRecords<ManhattanLocationData>().ToList();

                    // Convert the records into a dictionary using LocationExternalId as key
                    locationMappingDictionary = records.ToDictionary(r => r.LocationExternalId);
                }
            }
            catch (IOException ex)
            {
                // Handle file reading errors
                Console.Error.WriteLine($"Error reading the CSV file: {ex.Message}");
            }
            catch (CsvHelperException ex)
            {
                // Handle CSV parsing errors
                Console.Error.WriteLine($"Error parsing the CSV file: {ex.Message}");
            }

            return locationMappingDictionary;
        }


        /// <summary>
        /// Retrieves the latest file from the specified directory based on last modified date.
        /// </summary>
        /// <param name="directoryPath">The directory to search for files.</param>
        /// <returns>The path to the latest file, or null if no files are found.</returns>
        private static string GetLatestFile(string directoryPath)
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            var files = directoryInfo.GetFiles();

            // Return the file with the most recent LastWriteTime, or null if no files are found
            return files.OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
        }

        public string GenerateManhattanPunchXML(IEnumerable<ShiftGroup> groupedTimeClockData)
        {
            // Create XML Document
            XmlDocument xmlDoc = new XmlDocument();
            XmlElement root = xmlDoc.CreateElement("tXML");
            xmlDoc.AppendChild(root);

            // Add header elements
            AddHeaderElements(xmlDoc, root);

            XmlElement message = xmlDoc.CreateElement("Message");
            root.AppendChild(message);
            XmlElement timeAndAttendance = xmlDoc.CreateElement("TimeAndAttendance");
            message.AppendChild(timeAndAttendance);

            int tranNumber = 1;

            // Process each group of records
            foreach (var group in groupedTimeClockData)
            {
                if (group.Records.Any(r => r.EventType == "Create"))
                {
                    ProcessCreateEvent(xmlDoc, timeAndAttendance, ref tranNumber, group);
                }
                else if (group.Records.Any(r => r.EventType == "Delete"))
                {
                    ProcessDeleteEvent(xmlDoc, timeAndAttendance, ref tranNumber, group);
                }
            }

            // Save the XML to a file
            xmlDoc.Save("output.xml");
            return xmlDoc.OuterXml;
        }

        // Helper method to add header elements
        private void AddHeaderElements(XmlDocument xmlDoc, XmlElement root)
        {
            XmlElement header = xmlDoc.CreateElement("Header");
            root.AppendChild(header);

            header.AppendChild(CreateElement(xmlDoc, "Source", "Host"));
            header.AppendChild(CreateElement(xmlDoc, "Batch_ID", "BT23095"));
            header.AppendChild(CreateElement(xmlDoc, "Message_Type", "TAS"));
            header.AppendChild(CreateElement(xmlDoc, "Company_ID", "01"));
            header.AppendChild(CreateElement(xmlDoc, "Msg_Locale", "English (United States)"));
        }

        // Helper method to process the "Create" event
        private void ProcessCreateEvent(XmlDocument xmlDoc, XmlElement timeAndAttendance, ref int tranNumber, ShiftGroup group)
        {
            XmlElement tasData = xmlDoc.CreateElement("TASData");
            timeAndAttendance.AppendChild(tasData);
            XmlElement mergeRange = xmlDoc.CreateElement("MergeRange");
            tasData.AppendChild(mergeRange);

            mergeRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
            mergeRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.First().ManhattanWarehouseId));
            mergeRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.EmployeeExternalId.ToString()));

            DateTime? empClockIn = GetClockInTime(group, "ShiftBegin");
            DateTime? empClockOut = GetClockOutTime(group, "ShiftEnd");
            DateTime? breakClockIn = GetBreakClockInTime(group, "MealBreakBegin");
            DateTime? breakClockOut = GetBreakClockOutTime(group, "MealBreakEnd");

            DateTime? startDateForMerge = empClockIn?.AddHours(-2) ?? empClockOut?.AddHours(-2);
            DateTime? endDateForMerge = empClockOut?.AddHours(2) ?? empClockIn?.AddHours(2);

            mergeRange.AppendChild(CreateElement(xmlDoc, "StartDateForMerge", startDateForMerge?.ToString("MM/dd/yyyy HH:mm:ss")));
            mergeRange.AppendChild(CreateElement(xmlDoc, "EndDateForMerge", endDateForMerge?.ToString("MM/dd/yyyy HH:mm:ss")));

            XmlElement mergeClockInClockOut = xmlDoc.CreateElement("MergeClockInClockOut");
            mergeRange.AppendChild(mergeClockInClockOut);

            AppendClockInOutTimes(xmlDoc, mergeClockInClockOut, empClockIn, empClockOut);

            if (mealBreakFlag && (breakClockIn.HasValue || breakClockOut.HasValue))
            {
                AddBreakTimes(xmlDoc, mergeRange, breakClockIn, breakClockOut);
            }

            tranNumber++;
        }

        // Helper method to process the "Delete" event
        private void ProcessDeleteEvent(XmlDocument xmlDoc, XmlElement timeAndAttendance, ref int tranNumber, ShiftGroup group)
        {
            XmlElement tasData = xmlDoc.CreateElement("TASData");
            timeAndAttendance.AppendChild(tasData);
            XmlElement deleteClockInRange = xmlDoc.CreateElement("DeleteClockInRange");
            tasData.AppendChild(deleteClockInRange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.First().ManhattanWarehouseId));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.EmployeeExternalId.ToString()));

            DateTime? startDateForDel = group.Records.Min(r => r.ClockTimeBeforeChange);
            DateTime? endDateForDel = group.Records.Max(r => r.ClockTimeBeforeChange);

            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "StartDateForDel", startDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));
            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EndDateForDel", endDateForDel?.ToString("MM/dd/yyyy HH:mm:ss")));

            tranNumber++;
        }

        // Helper method to get clock-in time (either from "ApproveReject" or the earliest "ShiftBegin" time)
        private DateTime? GetClockInTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Min(r => r.ClockTimeAfterChange);
        }

        // Helper method to get clock-out time (either from "ApproveReject" or the latest "ShiftEnd" time)
        private DateTime? GetClockOutTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Max(r => r.ClockTimeAfterChange);
        }

        // Helper method to get break time (either from "ApproveReject" or the earliest "MealBreakBegin" or latest "MealBreakEnd")
        private DateTime? GetBreakClockInTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Min(r => r.ClockTimeAfterChange);
        }

        // Helper method to get break time (either from "ApproveReject" or the earliest "MealBreakBegin" or latest "MealBreakEnd")
        private DateTime? GetBreakClockOutTime(ShiftGroup group, string clockType)
        {
            var record = group.Records.FirstOrDefault(r => r.ClockType == clockType && r.EventType == "ApproveReject");
            if (record != null)
            {
                return record.ClockTimeAfterChange;
            }
            return group.Records.Where(r => r.ClockType == clockType).Max(r => r.ClockTimeAfterChange);
        }

        // Helper method to append clock-in and clock-out times
        private void AppendClockInOutTimes(XmlDocument xmlDoc, XmlElement mergeClockInClockOut, DateTime? empClockIn, DateTime? empClockOut)
        {
            if (empClockIn.HasValue)
            {
                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockIn", empClockIn.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            if (empClockOut.HasValue)
            {
                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockOut", empClockOut.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }
        }

        // Helper method to add break times to the XML
        private void AddBreakTimes(XmlDocument xmlDoc, XmlElement mergeRange, DateTime? breakClockIn, DateTime? breakClockOut)
        {
            XmlElement mergeBreakStartBreakEnd = xmlDoc.CreateElement("MergeBreakStartBreakEnd");
            mergeRange.AppendChild(mergeBreakStartBreakEnd);

            if (breakClockIn.HasValue)
            {
                mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakStartTime", breakClockIn.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            if (breakClockOut.HasValue)
            {
                mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakEndTime", breakClockOut.Value.ToString("MM/dd/yyyy HH:mm:ss")));
            }

            mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "Activity", "UNPAIDBRK"));
        }

        // Helper method to create XML elements
        private XmlElement CreateElement(XmlDocument xmlDoc, string name, string value)
        {
            XmlElement element = xmlDoc.CreateElement(name);
            element.InnerText = value;
            return element;
        }

        // Function to split records into groups based on a time gap
        static IEnumerable<ShiftGroup> SplitByTimeGap(IEnumerable<ClockRecord> records, TimeSpan maxGap)
        {
            var groups = new List<ShiftGroup>();
            ShiftGroup currentGroup = null;

            foreach (var record in records)
            {
                // Check if a new group needs to be created
                if (currentGroup == null || (record.ClockTimeAfterChange - currentGroup.Records[0].ClockTimeAfterChange) > maxGap)
                {
                    // Start a new group if no group exists or time gap exceeds threshold
                    currentGroup = new ShiftGroup
                    {
                        EmployeeExternalId = record.EmployeeExternalId
                    };
                    groups.Add(currentGroup);
                }

                // Add the current record to the group
                currentGroup.Records.Add(record);
            }

            return groups;
        }


        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            string timestamp = startTime.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");

            try
            {
                // Validate if the source file exists
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"The file does not exist: {filePath}");
                }

                ReadClockRecordsFromFileAndInsertToDatabase(filePath);

                // Read and process CSV records lazily and asynchronously
                var groupedRecords = await GetGroupedRecordsFromDatabaseAsync();//await GetGroupedRecordsAsync(filePath);

                // Prepare a list of ShiftGroups to pass to XML generation
                var allGroups = new List<ShiftGroup>();

                // Process each employee's records (grouped by EmployeeExternalId and EventTypeGroup)
                foreach (var employeeGroup in groupedRecords)
                {
                    // Split the employee's records into groups based on a 14-hour time gap
                    var groupedByTimeGap = SplitByTimeGap(employeeGroup.OrderBy(r => r.ClockTimeAfterChange), TimeSpan.FromHours(14));

                    // Add to the list of all groups
                    allGroups.AddRange(groupedByTimeGap);
                }

                // Generate XML for all groups
                GenerateManhattanPunchXML(allGroups);

                // Log processing completion details
                DateTime endTime = DateTime.Now;
                LoggerObserver.LogFileProcessed($"Finished processing Manhattan punch CSV: {filePath} at {endTime}");
                TimeSpan duration = endTime - startTime;
                LoggerObserver.LogFileProcessed($"Time taken to process the file: {duration.TotalSeconds:F2} seconds.");
            }
            catch (FileNotFoundException ex)
            {
                LoggerObserver.Error(ex, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                LoggerObserver.Error(ex, "Unauthorized access during file processing.");
            }
            catch (IOException ex)
            {
                LoggerObserver.Error(ex, "I/O error occurred during file processing.");
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "An unexpected error occurred during processing.");
                throw; // Re-throw the exception to ensure proper visibility of critical errors
            }
        }

        private async Task<IEnumerable<IGrouping<object, ClockRecord>>> GetGroupedRecordsFromDatabaseAsync()
        {
            string connectionString = "Data Source=fivebelow_integration.db";

            // Change the type to IEnumerable<IGrouping<object, ClockRecord>>
            IEnumerable<IGrouping<object, ClockRecord>> groupedRecords = null;

            using (var connection = new SQLiteConnection(connectionString))
            {
                await connection.OpenAsync();

                // Fetch records from the clock_record table
                var query = @"
                    WITH CurrentRecords AS (
                        SELECT *
                        FROM clock_record
                        WHERE is_current = 1
                    ),
                    OldRecords AS (
                        SELECT *
                        FROM clock_record
                        WHERE is_current = 0
                    ),
                    TimeDifferences AS (
                        SELECT 
                            cr.employee_external_id,
                            cr.clock_time_after_change AS ClockTime1,
                            orr.clock_time_after_change AS ClockTime2,
                            ABS(STRFTIME('%s', orr.clock_time_after_change) - STRFTIME('%s', cr.clock_time_after_change)) / 3600 AS HourDifference
                        FROM CurrentRecords cr
                        INNER JOIN OldRecords orr
                            ON cr.employee_external_id = orr.employee_external_id
                            AND ABS(STRFTIME('%s', orr.clock_time_after_change) - STRFTIME('%s', cr.clock_time_after_change)) / 3600 <= 14
                    )
                    SELECT DISTINCT 
                        r.*
                    FROM clock_record r
                    LEFT JOIN (
                        SELECT 
                            cr.employee_external_id,
                            cr.clock_time_after_change AS ClockTime
                        FROM CurrentRecords cr
                        UNION ALL
                        SELECT 
                            td.employee_external_id,
                            td.ClockTime2 AS ClockTime
                        FROM TimeDifferences td
                    ) filtered_records
                        ON r.employee_external_id = filtered_records.employee_external_id
                        AND r.clock_time_after_change = filtered_records.ClockTime
                    WHERE r.is_current = 1 
                       OR (r.employee_external_id IN (SELECT employee_external_id FROM CurrentRecords)
                           AND r.is_current = 0
                           AND r.clock_time_after_change IN (SELECT ClockTime2 FROM TimeDifferences))
                    ORDER BY r.employee_external_id, r.clock_time_after_change;

                ";


                using (var command = new SQLiteCommand(query, connection))
                {
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        var records = new List<ClockRecord>();

                        // Parse the results into a list of ClockRecord objects
                        while (await reader.ReadAsync())
                        {
                            var clockRecord = new ClockRecord
                            {
                                LocationExternalId = reader["location_external_id"]?.ToString(),
                                EmployeeExternalId = Convert.ToInt32(reader["employee_external_id"]),
                                ClockType = reader["clock_type"]?.ToString(),
                                ClockTimeBeforeChange = reader["clock_time_before_change"] == DBNull.Value
                                    ? (DateTime?)null
                                    : DateTime.Parse(reader["clock_time_before_change"].ToString()),
                                ClockTimeAfterChange = reader["clock_time_after_change"] == DBNull.Value
                                    ? (DateTime?)null
                                    : DateTime.Parse(reader["clock_time_after_change"].ToString()),
                                ClockWorkRoleAfterChange = reader["clock_work_role_after_change"]?.ToString(),
                                EventType = reader["event_type"]?.ToString(),
                                //LocationName = reader["location_name"]?.ToString(),
                                //ManhattanWarehouseId = reader["manhattan_warehouse_id"]?.ToString()
                            };

                            // Perform any additional mappings (e.g., LocationMapping and TimeZoneMapping)
                            if (LocationMapping.TryGetValue(clockRecord.LocationExternalId, out var locationData))
                            {
                                clockRecord.LocationName = locationData.LocationName;
                                clockRecord.ManhattanWarehouseId = locationData.ManhattanWarehouseId;
                            }

                            if (TimeZoneMapping.TryGetValue(clockRecord.LocationExternalId, out var timeZoneData) && !string.IsNullOrWhiteSpace(timeZoneData.TimeZone))
                            {
                                try
                                {
                                    var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneData.TimeZone);
                                    clockRecord.ClockTimeBeforeChange = ConvertToLocalTime(clockRecord.ClockTimeBeforeChange, timeZoneInfo);
                                    clockRecord.ClockTimeAfterChange = ConvertToLocalTime(clockRecord.ClockTimeAfterChange, timeZoneInfo);
                                }
                                catch (TimeZoneNotFoundException)
                                {
                                    LoggerObserver.LogFileProcessed($"Invalid TimeZone: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                                }
                                catch (InvalidTimeZoneException)
                                {
                                    LoggerObserver.LogFileProcessed($"Invalid TimeZone data: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                                }
                            }

                            records.Add(clockRecord);
                        }

                        // Group records by EmployeeExternalId and EventTypeGroup
                        groupedRecords = records
                            .GroupBy(r => new
                            {
                                r.EmployeeExternalId,
                                EventTypeGroup = (r.EventType == "Create" || r.EventType == "ApproveReject")
                                    ? "Create_ApproveReject"
                                    : r.EventType
                            });
                    }
                }
            }

            return groupedRecords;
        }




        public void ReadClockRecordsFromFileAndInsertToDatabase(string filePath)
        {
            string connectionString = "Data Source=fivebelow_integration.db";
            const int batchSize = 1000;

            // List to hold the result set as ClockRecord objects
            var clockRecords = new List<ClockRecord>();

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                using (var resetCommand = new SQLiteCommand("UPDATE clock_record SET is_current = 0", connection))
                {
                    resetCommand.ExecuteNonQuery();
                }

                SQLiteTransaction transaction = connection.BeginTransaction();

                try
                {
                    var insertCommand = @"
                        INSERT INTO clock_record (
                        location_external_id,
                        employee_external_id,
                        clock_type,
                        clock_time_before_change,
                        clock_time_after_change,
                        clock_work_role_after_change,
                        event_type,
                        location_name,
                        manhattan_warehouse_id,
                        is_current
                        )
                        SELECT
                            @LocationExternalId,
                            @EmployeeExternalId,
                            @ClockType,
                            @ClockTimeBeforeChange,
                            @ClockTimeAfterChange,
                            @ClockWorkRoleAfterChange,
                            @EventType,
                            @LocationName,
                            @ManhattanWarehouseId,
                            @IsCurrent
                        WHERE NOT EXISTS (
                            SELECT 1 FROM clock_record
                            WHERE location_external_id = @LocationExternalId
                              AND employee_external_id = @EmployeeExternalId  
                              AND clock_type = @ClockType                       
                              AND event_type = @EventType
                              AND location_name = @LocationName
                              AND (
                                  (clock_time_after_change = @ClockTimeAfterChange OR (clock_time_after_change IS NULL AND @ClockTimeAfterChange IS NULL))
                              )
                              AND (
                                  (clock_time_before_change = @ClockTimeBeforeChange OR (clock_time_before_change IS NULL AND @ClockTimeBeforeChange IS NULL))
                              )
                        );
                    ";

                    using (var command = new SQLiteCommand(insertCommand, connection, transaction))
                    {
                        using (var reader = new StreamReader(filePath))
                        {
                            reader.ReadLine(); // Skip header
                            int batchCount = 0;
                            string line;

                            while ((line = reader.ReadLine()) != null)
                            {
                                var parts = line.Split(',');

                                command.Parameters.Clear();
                                command.Parameters.AddWithValue("@LocationExternalId", parts[2]?.Trim());
                                command.Parameters.AddWithValue("@EmployeeExternalId", int.Parse(parts[7]?.Trim()));
                                command.Parameters.AddWithValue("@ClockType", parts[9]?.Trim());
                                command.Parameters.AddWithValue("@ClockTimeBeforeChange", string.IsNullOrWhiteSpace(parts[10])
                                    ? null
                                    : DateTime.Parse(parts[10], CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss"));
                                command.Parameters.AddWithValue("@ClockTimeAfterChange", string.IsNullOrWhiteSpace(parts[17])
                                    ? null
                                    : DateTime.Parse(parts[17], CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm:ss"));
                                command.Parameters.AddWithValue("@ClockWorkRoleAfterChange", parts[21]?.Trim());
                                command.Parameters.AddWithValue("@EventType", parts[24]?.Trim());
                                command.Parameters.AddWithValue("@LocationName", parts[3]?.Trim());
                                command.Parameters.AddWithValue("@ManhattanWarehouseId", parts[4]?.Trim());
                                command.Parameters.AddWithValue("@IsCurrent", 1);

                                command.ExecuteNonQuery();
                                batchCount++;

                                if (batchCount >= batchSize)
                                {
                                    transaction.Commit();
                                    transaction.Dispose();
                                    transaction = connection.BeginTransaction();
                                    batchCount = 0;
                                }
                            }

                            if (batchCount > 0)
                            {
                                transaction.Commit();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error occurred: {ex.Message}");
                    transaction.Rollback();
                    throw;
                }
                finally
                {
                    transaction.Dispose();
                }
            }
        }

        private async Task<IEnumerable<IGrouping<object, ClockRecord>>> GetGroupedRecordsAsync(string filePath)
        {
            // Use lazy loading to read and process the file line by line
            IEnumerable<ClockRecord> records = ReadClockRecordsFromFile(filePath);

            // Perform LINQ to join with LocationMapping and TimeZoneMapping
            var joinedRecords = records.Select(clockRecord =>
            {
                // Fetch LocationName and ManhattanWarehouseId from LocationMapping
                if (LocationMapping.TryGetValue(clockRecord.LocationExternalId, out var locationData))
                {
                    clockRecord.LocationName = locationData.LocationName;
                    clockRecord.ManhattanWarehouseId = locationData.ManhattanWarehouseId; // Assuming WarehouseId is correct field name
                }
                else
                {
                    LoggerObserver.LogFileProcessed($"Location mapping not found for LocationExternalId: {clockRecord.LocationExternalId}");
                }

                // Fetch TimeZone from TimeZoneMapping and convert UTC times to local times
                if (TimeZoneMapping.TryGetValue(clockRecord.LocationExternalId, out var timeZoneData))
                {
                    if (!string.IsNullOrWhiteSpace(timeZoneData.TimeZone))
                    {
                        try
                        {
                            var timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneData.TimeZone);
                            clockRecord.ClockTimeBeforeChange = ConvertToLocalTime(clockRecord.ClockTimeBeforeChange, timeZoneInfo);
                            clockRecord.ClockTimeAfterChange = ConvertToLocalTime(clockRecord.ClockTimeAfterChange, timeZoneInfo);
                        }
                        catch (TimeZoneNotFoundException)
                        {
                            LoggerObserver.LogFileProcessed($"Invalid TimeZone: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                        }
                        catch (InvalidTimeZoneException)
                        {
                            LoggerObserver.LogFileProcessed($"Invalid TimeZone data: {timeZoneData.TimeZone} for LocationExternalId: {clockRecord.LocationExternalId}");
                        }
                    }
                }
                else
                {
                    LoggerObserver.LogFileProcessed($"TimeZone mapping not found for LocationExternalId: {clockRecord.LocationExternalId}");
                }

                return clockRecord;
            });

            // Group records by EmployeeExternalId and EventTypeGroup using LINQ
            var groupedRecords = joinedRecords
                .GroupBy(r => new
                {
                    r.EmployeeExternalId,
                    EventTypeGroup = (r.EventType == "Create" || r.EventType == "ApproveReject")
                                        ? "Create_ApproveReject"
                                        : r.EventType
                });

            return groupedRecords;
        }

        // Lazy load ClockRecords from the file
        private IEnumerable<ClockRecord> ReadClockRecordsFromFile(string filePath)
        {
            using (var reader = new StreamReader(filePath))
            {
                // Skip the header
                reader.ReadLine();

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = line.Split(',');

                    // Yield each record as it is read and parsed
                    yield return new ClockRecord
                    {
                        LocationExternalId = parts[2],
                        EmployeeExternalId = int.Parse(parts[7]),
                        ClockType = parts[9],
                        ClockTimeBeforeChange = string.IsNullOrWhiteSpace(parts[10])
                                                    ? (DateTime?)null
                                                    : DateTime.Parse(parts[10], CultureInfo.InvariantCulture),
                        ClockTimeAfterChange = string.IsNullOrWhiteSpace(parts[17])
                                                    ? (DateTime?)null
                                                    : DateTime.Parse(parts[17], CultureInfo.InvariantCulture),
                        ClockWorkRoleAfterChange = parts[21],
                        EventType = parts[24]
                    };
                }
            }
        }

        // Helper method to convert UTC time to local time based on TimeZoneInfo
        private DateTime? ConvertToLocalTime(DateTime? utcTime, TimeZoneInfo timeZoneInfo)
        {
            if (utcTime.HasValue)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(utcTime.Value, timeZoneInfo);
            }
            return null;
        }

    }
}
