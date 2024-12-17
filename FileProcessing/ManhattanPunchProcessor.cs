using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json.Linq;
using ProcessFiles_Demo.DataModel;
using ProcessFiles_Demo.Helpers;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.SFTPExtract;
using System;
using System.Collections.Generic;
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
        private Dictionary<string, string> accrualMemoCodeMapping;
        private Dictionary<string, List<PaycodeData>> paycodeDict;
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();
        ExtractLocationEntityData extractLocationEntityData = new ExtractLocationEntityData();
        private readonly HashSet<string> payrollProcessedFileNumbers;
        private bool mealBreakFlag = false;


        public ManhattanPunchProcessor(JObject clientSettings)
        {
            var payroll_clientSettings = ClientSettingsLoader.LoadClientSettings("payroll");
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            mealBreakFlag = bool.TryParse(clientSettings["Flags"]["MealBrakRequired"]?.ToString(), out bool flag) && flag;
            //string remoteMappingFilePath = clientSettings["Folders"]["remoteLocationEntityPath"].ToString();
            //string locationEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath, "LocationEntity");
            // Load employee HR mapping from Excel (grouped by employee ID now)
            LocationMapping = LoadLocationMapping("location mapping.csv");                
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

        // Helper method to create XML elements
        static XmlElement CreateElement(XmlDocument xmlDoc, string name, string value)
        {
            XmlElement element = xmlDoc.CreateElement(name);
            element.InnerText = value;
            return element;
        }

        public string GenerateManhattanPunchXML(IEnumerable<ShiftGroup> groupedTimeClockData)
        {
            // Create XML Document
            XmlDocument xmlDoc = new XmlDocument();

            // Create root element <tXML>
            XmlElement root = xmlDoc.CreateElement("tXML");
            xmlDoc.AppendChild(root);

            // Create Header element
            XmlElement header = xmlDoc.CreateElement("Header");
            root.AppendChild(header);

            header.AppendChild(CreateElement(xmlDoc, "Source", "Host"));
            header.AppendChild(CreateElement(xmlDoc, "Batch_ID", "BT23095"));
            header.AppendChild(CreateElement(xmlDoc, "Message_Type", "TAS"));
            header.AppendChild(CreateElement(xmlDoc, "Company_ID", "01"));
            header.AppendChild(CreateElement(xmlDoc, "Msg_Locale", "English (United States)"));

            // Create Message element
            XmlElement message = xmlDoc.CreateElement("Message");
            root.AppendChild(message);

            // Create TimeAndAttendance element
            XmlElement timeAndAttendance = xmlDoc.CreateElement("TimeAndAttendance");
            message.AppendChild(timeAndAttendance);

            // Initialize a counter for TranNumber
            int tranNumber = 1;

            // Process each group of records
            foreach (var group in groupedTimeClockData)
            {
                foreach (var eventType in new[] { "Create", "Delete" })
                {
                    if (group.Records.Any(r => r.EventType == eventType))
                    {
                        XmlElement tasData = xmlDoc.CreateElement("TASData");
                        timeAndAttendance.AppendChild(tasData);

                        if (eventType == "Create")
                        {
                            XmlElement mergeRange = xmlDoc.CreateElement("MergeRange");
                            tasData.AppendChild(mergeRange);

                            mergeRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
                            mergeRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.FirstOrDefault().ManhattanWarehouseId));
                            mergeRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.EmployeeExternalId.ToString()));
                            DateTime? empClockIn = null;
                            DateTime? empClockOut = null;
                            DateTime? BreakClockIn = null;
                            DateTime? BreakClockOut = null;

                            // Check if there is any record with EventType "ApproveReject" in ShiftEnd
                            var approveRejectRecord_ShiftBegin = group.Records
                                .FirstOrDefault(r => r.ClockType == "ShiftBegin" && r.EventType == "ApproveReject");

                            if (approveRejectRecord_ShiftBegin != null)
                            {
                                // If "ApproveReject" record is found, fetch its ClockTimeAfterChange
                                empClockIn = approveRejectRecord_ShiftBegin.ClockTimeAfterChange;
                            }
                            else
                            {
                                // If no "ApproveReject" record, fetch the max ClockTimeAfterChange for ShiftEnd
                                empClockIn = group.Records
                                    .Where(r => r.ClockType == "ShiftBegin")
                                    .Min(r => r.ClockTimeAfterChange);
                            }

                            // Check if there is any record with EventType "ApproveReject" in ShiftEnd
                            var approveRejectRecord_ShiftEnd= group.Records
                                .FirstOrDefault(r => r.ClockType == "ShiftEnd" && r.EventType == "ApproveReject");

                            if (approveRejectRecord_ShiftEnd != null)
                            {
                                // If "ApproveReject" record is found, fetch its ClockTimeAfterChange
                                empClockOut = approveRejectRecord_ShiftEnd.ClockTimeAfterChange;
                            }
                            else
                            {
                                // If no "ApproveReject" record, fetch the max ClockTimeAfterChange for ShiftEnd
                                empClockOut = group.Records
                                    .Where(r => r.ClockType == "ShiftEnd")
                                    .Max(r => r.ClockTimeAfterChange);
                            }


                            // Check if there is any record with EventType "ApproveReject" in ShiftEnd
                            var approveRejectRecord_BreakBegin = group.Records
                                .FirstOrDefault(r => r.ClockType == "MealBreakBegin" && r.EventType == "ApproveReject");

                            if (approveRejectRecord_BreakBegin != null)
                            {
                                // If "ApproveReject" record is found, fetch its ClockTimeAfterChange
                                BreakClockIn = approveRejectRecord_BreakBegin.ClockTimeAfterChange;
                            }
                            else
                            {
                                // If no "ApproveReject" record, fetch the max ClockTimeAfterChange for ShiftEnd
                                BreakClockIn = group.Records
                                    .Where(r => r.ClockType == "MealBreakBegin")
                                    .Min(r => r.ClockTimeAfterChange);
                            }

                            // Check if there is any record with EventType "ApproveReject" in ShiftEnd
                            var approveRejectRecord_BreakEnd = group.Records
                                .FirstOrDefault(r => r.ClockType == "MealBreakEnd" && r.EventType == "ApproveReject");

                            if (approveRejectRecord_BreakEnd != null)
                            {
                                // If "ApproveReject" record is found, fetch its ClockTimeAfterChange
                                BreakClockOut = approveRejectRecord_BreakEnd.ClockTimeAfterChange;
                            }
                            else
                            {
                                // If no "ApproveReject" record, fetch the max ClockTimeAfterChange for ShiftEnd
                                BreakClockOut = group.Records
                                    .Where(r => r.ClockType == "MealBreakEnd")
                                    .Max(r => r.ClockTimeAfterChange);
                            }

                            DateTime? startDateForMerge = empClockIn?.AddHours(-2) ?? empClockOut?.AddHours(-2);
                            DateTime? endDateForMerge = empClockOut?.AddHours(2) ?? empClockIn?.AddHours(2);

                            mergeRange.AppendChild(CreateElement(xmlDoc, "StartDateForMerge", startDateForMerge?.ToString("o")));
                            mergeRange.AppendChild(CreateElement(xmlDoc, "EndDateForMerge", endDateForMerge?.ToString("o")));

                            XmlElement mergeClockInClockOut = xmlDoc.CreateElement("MergeClockInClockOut");
                            mergeRange.AppendChild(mergeClockInClockOut);

                            if (empClockIn.HasValue)
                            {
                                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockIn", empClockIn.Value.ToString("o")));
                            }

                            if (empClockOut.HasValue)
                            {
                                mergeClockInClockOut.AppendChild(CreateElement(xmlDoc, "EmpClockOut", empClockOut.Value.ToString("o")));
                            }

                            if (mealBreakFlag && (BreakClockIn.HasValue || BreakClockOut.HasValue))
                            {
                                XmlElement mergeBreakStartBreakEnd = xmlDoc.CreateElement("MergeBreakStartBreakEnd");
                                mergeRange.AppendChild(mergeBreakStartBreakEnd);

                                if (BreakClockIn.HasValue)
                                {
                                    mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakStartTime", BreakClockIn.Value.ToString("o")));
                                }

                                if (BreakClockOut.HasValue)
                                {
                                    mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "BreakEndTime", BreakClockOut.Value.ToString("o")));
                                }

                                mergeBreakStartBreakEnd.AppendChild(CreateElement(xmlDoc, "Activity", "UNPAIDBRK"));
                            }

                        }
                        else if (eventType == "Delete")
                        {
                            XmlElement deleteClockInRange = xmlDoc.CreateElement("DeleteClockInRange");
                            tasData.AppendChild(deleteClockInRange);

                            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "TranNumber", tranNumber.ToString("D9")));
                            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "Warehouse", group.Records.FirstOrDefault().ManhattanWarehouseId));
                            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EmployeeUserId", group.EmployeeExternalId.ToString()));

                            DateTime? startDateForDel = group.Records.Min(r => r.ClockTimeBeforeChange);
                            DateTime? endDateForDel = group.Records.Max(r => r.ClockTimeBeforeChange);

                            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "StartDateForDel", startDateForDel?.ToString("o")));
                            deleteClockInRange.AppendChild(CreateElement(xmlDoc, "EndDateForDel", endDateForDel?.ToString("o")));
                        }

                        // Increment the TranNumber for the next group
                        tranNumber++;
                    }
                }
            }

            // Save the XML to a file
            xmlDoc.Save("output.xml");
            return xmlDoc.OuterXml;
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

                // Read and process CSV records in a memory-efficient way
                var records = File.ReadLines(filePath)
                                  .Skip(1) // Skip header row
                                  .Select(line =>
                                  {
                                      var parts = line.Split(',');
                                      var clockRecord = new ClockRecord
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
                                          EventType = parts[24],
                                      };

                                      // Perform the join here with LocationMapping
                                      var locationData = LocationMapping
                                          .FirstOrDefault(x => x.Key == clockRecord.LocationExternalId).Value;

                                      // If location data is found, assign it to the clock record
                                      if (locationData != null)
                                      {
                                          clockRecord.LocationName = locationData.LocationName;  // Assign LocationName from the dictionary
                                          clockRecord.ManhattanWarehouseId = locationData.ManhattanWarehouseId;  // Assign ManhattanWarehouseID from the dictionary
                                      }
                                      else
                                      {
                                          LoggerObserver.LogFileProcessed($"Location mapping not found for LocationExternalId: {clockRecord.LocationExternalId}");
                                      }

                                      return clockRecord; // Return the ClockRecord with joined LocationData
                                  })
                                  .GroupBy(r => new { r.EmployeeExternalId, r.EventType }) // Group records by Employee ID and Event Type
                                  .ToList(); // ToList() to materialize the grouped results


                // Prepare a list of ShiftGroups to pass to XML generation
                var allGroups = new List<ShiftGroup>();

                // Process each employee's records
                foreach (var employeeGroup in records)
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

        private async Task WriteBatchAsync(string destinationFilePath, List<string> lineBuffer)
        {
            using (var writer = new StreamWriter(destinationFilePath, true))
            {
                foreach (var line in lineBuffer)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
    }
}
