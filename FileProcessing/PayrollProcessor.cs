using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OfficeOpenXml;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.DataModel;
using CsvHelper;
using ProcessFiles_Demo.SFTPExtract;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PayrollFileProcessor : ICsvFileProcessorStrategy
    {
        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, EmployeeHrData> employeeHrMapping;        
        private Dictionary<string, PaycodeData> payCodeMap; // Updated type
        private Dictionary<string, List<PaycodeData>> paycodeDict;
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();
       

        public PayrollFileProcessor(JObject clientSettings)
        {
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            string remoteMappingFilePath = "/home/fivebelow-uat/outbox/extracts";
            string employeeEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath);
            // Load employee HR mapping from Excel (grouped by employee ID now)
            employeeHrMapping = LoadGroupedEmployeeHrMappingFromCsv(employeeEntityMappingPath);
            paycodeDict = LoadPaycodeMappingFromXlsx("LegionPayCodes.xlsx");

        }


        // Optimized method to load and group employee HR data by EmployeeExternalId from CSV
        private Dictionary<string, EmployeeHrData> LoadGroupedEmployeeHrMappingFromCsv(string filePath)
        {
            var hrMapping = new Dictionary<string, EmployeeHrData>();

            using (var reader = new StreamReader(filePath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                csv.Read();
                csv.ReadHeader();

                while (csv.Read())
                {
                    string employeeExternalId = csv.GetField("EmployeeExternalId");

                    if (!string.IsNullOrEmpty(employeeExternalId))
                    {
                        EmployeeHrData hrData = new EmployeeHrData
                        {
                            EmployeeId = csv.GetField("EmployeeId"),
                            EmployeeExternalId = employeeExternalId,
                            LegionUserId = Convert.ToInt64(csv.GetField("LegionUserId")),
                            LocationId = csv.GetField("LocationId"),
                            LocationExternalId = csv.GetField("LocationExternalId"),
                            LastModifiedDate = DateTime.Parse(csv.GetField("LastModifiedDate")),
                            FirstName = csv.GetField("FirstName"),
                            LastName = csv.GetField("LastName"),
                            MiddleInitial = csv.GetField("MiddleInitial"),
                            NickName = csv.GetField("NickName"),
                            Title = csv.GetField("Title"),
                            Email = csv.GetField("Email"),
                            PhoneNumber = csv.GetField("PhoneNumber"),
                            Status = csv.GetField("Status"),
                            ManagerId = csv.GetField("ManagerId"),
                            Salaried = csv.GetField<bool>("Salaried"),
                            Hourly = csv.GetField<bool>("Hourly"),
                            Exempt = csv.GetField<bool>("Exempt"),
                            HourlyRate = decimal.TryParse(csv.GetField("HourlyRate"), out var hourlyRate) ? hourlyRate : 0,
                            LegionUserFirstName = csv.GetField("LegionUserFirstName"),
                            LegionUserLastName = csv.GetField("LegionUserLastName"),
                            LegionUserNickName = csv.GetField("LegionUserNickName"),
                            LegionUserEmail = csv.GetField("LegionUserEmail"),
                            LegionUserPhoneNumber = csv.GetField("LegionUserPhoneNumber"),
                            LegionUserAddress = csv.GetField("LegionUserAddress"),
                            LegionUserPhoto = csv.GetField("LegionUserPhoto"),
                            LegionUserBusinessPhoto = csv.GetField("LegionUserBusinessPhoto"),
                            CompanyId = csv.GetField("CompanyId"),
                            CompanyName = csv.GetField("CompanyName")
                        };

                        // Add to the dictionary by EmployeeExternalId
                        if (!hrMapping.ContainsKey(employeeExternalId))
                        {
                            hrMapping[employeeExternalId] = hrData;
                        }
                    }
                }
            }

            // Sort the dictionary by EmployeeExternalId for performance improvement
            var sortedHrMapping = new SortedDictionary<string, EmployeeHrData>(hrMapping);
            return new Dictionary<string, EmployeeHrData>(sortedHrMapping);
        }

        public static (DateTime? StartDate, DateTime? EndDate) ExtractDateRange(string fileName)
        {
            // Define regex to capture the two dates in the format yyyy-MM-dd
            var match = Regex.Match(fileName, @"\d{4}-\d{2}-\d{2}-\d{4}-\d{2}-\d{2}");

            if (match.Success)
            {
                // Split the matched string to get start and end dates
                var dates = match.Value.Split('-');

                string startDateString = $"{dates[0]}-{dates[1]}-{dates[2]}"; // 2024-10-20
                string endDateString = $"{dates[3]}-{dates[4]}-{dates[5]}";   // 2024-11-02

                DateTime startDate = DateTime.ParseExact(startDateString, "yyyy-MM-dd", null);
                DateTime endDate = DateTime.ParseExact(endDateString, "yyyy-MM-dd", null);                
                return (startDate, endDate);
            }
            else
            {
                LoggerObserver.Info("Date range not found in the filename.");
                return (null, null);
            }
        }



        // Method to load and group paycode data from an excel file
        public Dictionary<string, List<PaycodeData>> LoadPaycodeMappingFromXlsx(string filePath)
        {
            var paycodeDict = new Dictionary<string, List<PaycodeData>>();

            // Set the LicenseContext for EPPlus (required)
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                // Get the first worksheet
                ExcelWorksheet worksheet = package.Workbook.Worksheets[0];
                int rowCount = worksheet.Dimension.Rows; // Get number of rows

                // Start reading from row 2 (skipping the header)
                for (int row = 2; row <= rowCount; row++)
                {
                    var paycode = new PaycodeData
                    {
                        PayType = worksheet.Cells[row, 1].Text.Trim(),
                        PayName = worksheet.Cells[row, 2].Text.Trim(),
                        Reference = worksheet.Cells[row, 3].Text.Trim(),
                        ADPColumn = worksheet.Cells[row, 4].Text.Trim(),
                        ADPHoursOrAmountCode = worksheet.Cells[row, 5].Text.Trim(),
                        PassForHourly = worksheet.Cells[row, 6].Text.Trim(),
                        PassForSalary = worksheet.Cells[row, 7].Text.Trim()
                    };

                    // Ensure PayType is not empty
                    if (!string.IsNullOrWhiteSpace(paycode.PayType))
                    {
                        // If the pay type already exists in the dictionary, add to the list
                        if (paycodeDict.ContainsKey(paycode.PayType))
                        {
                            paycodeDict[paycode.PayType].Add(paycode);
                        }
                        else
                        {
                            // If pay type doesn't exist, create a new list and add it to the dictionary
                            paycodeDict[paycode.PayType] = new List<PaycodeData> { paycode };
                        }
                    }
                }
            }

            return paycodeDict;
        }

        // Helper method to determine the memoAmount
        private (string memoCode, int memoAmount, string specialProcCode, DateTime? otherStartDate, DateTime? otherEndDate) GetMemoAmount(DateTime startDate, DateTime endDate, DateTime fileStartDate, DateTime week1EndDate, DateTime week2StartDate, DateTime fileEndDate)
        {
            if (startDate >= fileStartDate && endDate <= week1EndDate)
            {
                return ("WK",1, "",null,null); // Week 1
            }
            else if (startDate >= week2StartDate && endDate <= fileEndDate)
            {
                return ("WK",2, "",null,null); // Week 2
            }
            else
            {
                return ("",0, "E", startDate, endDate);
            }
        }

        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            var (fileStartDate, fileEndDate) = ExtractDateRange(Path.GetFileNameWithoutExtension(filePath));
            // Calculate Week 1 and Week 2 date ranges
            var week1EndDate = Convert.ToDateTime(fileStartDate).AddDays(6); // End of Week 1
            var week2StartDate = week1EndDate.AddDays(1); // Start of Week 2

            DateTime startTime = DateTime.Now;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");

            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"Payroll_{timestamp}.csv");

            string header = "Co Code,Batch ID,File #,Rate Code,Temp Dept,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount,Earnings 3 Code,Earnings 3 Amount,Memo Code,Memo Amount,Special Proc Code,Other Begin Date,Other End Date";
            using (var writer = new StreamWriter(destinationFilePath, false))
            {
                await writer.WriteLineAsync(header).ConfigureAwait(false);
            }

            var records = new List<PayrollRecord>();

            using (var reader = new StreamReader(filePath))
            {
                // Read and skip the header line
                string headerLine = await reader.ReadLineAsync().ConfigureAwait(false);

                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var payrollRecord = ParseLineToPayrollRecord(line);
                    if (payrollRecord != null)
                    {
                        records.Add(payrollRecord);
                    }
                    else
                    {
                        LoggerObserver.OnFileFailed($"Malformed line: {line}");
                    }
                }
            }

            // Sort by Employee Id and Date
            // Step 1: Group records by EmployeeId, date range, PayType, and PayrollEarningRole
            var initialGroupedRecords = records
                .Where(r => !(r.PayType == "Regular" && r.WorkRole == "Store Guard"))
                .OrderBy(r => r.EmployeeId)
                .ThenBy(r => ParseDateRange(r.Date).startDate)
                .GroupBy(r =>
                {
                    var dateRange = ParseDateRange(r.Date);
                    return new { r.EmployeeId, r.WorkLocation, StartDate = dateRange.startDate, EndDate = dateRange.endDate, r.PayType, r.PayRollEarningRole };
                })
                .Select(g =>
                {
                    // Get memoCode and memoAmount based on the date range
                    var (memoCode, memoAmount, specialProcCode, otherStartDate, otherEndDate) =
                        GetMemoAmount(g.Key.StartDate, g.Key.EndDate, Convert.ToDateTime(fileStartDate), week1EndDate, week2StartDate, Convert.ToDateTime(fileEndDate));

                    return new PayrollRecord
                    {
                        EmployeeId = g.Key.EmployeeId,
                        Date = g.Key.StartDate == g.Key.EndDate
                                ? g.Key.StartDate.ToString("M/d/yyyy")
                                : $"{g.Key.StartDate:M/d/yyyy} to {g.Key.EndDate:M/d/yyyy}",
                        PayType = g.Key.PayType,
                        EmployeeName = g.First().EmployeeName,
                        HomeLocation = g.First().HomeLocation,
                        JobTitle = g.First().JobTitle,
                        WorkLocation = g.Key.WorkLocation,
                        WorkRole = g.First().WorkRole,
                        PayName = string.Join("~", g.Select(r => r.PayName)),
                        PayRollEarningRole = g.First().PayRollEarningRole,
                        MemoCode = memoCode,
                        MemoAmount = memoAmount,
                        SpecialProcCode = specialProcCode,
                        OtherStartDate = Convert.ToString(otherStartDate),
                        OtherEndDate = Convert.ToString(otherEndDate),

                        Hours = g.Sum(r => r.Hours),
                        Rate = g.Sum(r => r.Rate),
                        Amount = g.Sum(r => r.Amount)
                    };
                })
                .ToList();

            // Step 2: Apply custom logic for Hours adjustment based on conditions
            var finalRecords = initialGroupedRecords
                .GroupBy(r => new { r.EmployeeId, r.Date, r.WorkLocation }) // Group by EmployeeId, Date, and WorkLocation
                .Select(g =>
                {
                    decimal adjustedHours = g.Sum(r => r.Hours); // Default sum of hours

                    // Condition 1: Overtime and Differential with 2SDOT
                    var regularRecord = g.FirstOrDefault(r => r.PayType == "Regular");
                    var differentialRecord = g.FirstOrDefault(r => r.PayType == "Differential" && r.PayRollEarningRole == "2SD");

                    if (regularRecord != null && differentialRecord != null)
                    {
                        regularRecord.Hours = regularRecord.Hours - differentialRecord.Hours; ; // Update Hours in Overtime record
                    }


                    // Condition 1: Overtime and Differential with 2SDOT
                    var overtimeRecord = g.FirstOrDefault(r => r.PayType == "Overtime");
                    var differentialOTRecord = g.FirstOrDefault(r => r.PayType == "Differential" && r.PayRollEarningRole == "2SDOT");

                    if (overtimeRecord != null && differentialOTRecord != null)
                    {                        
                        overtimeRecord.Hours = overtimeRecord.Hours - differentialOTRecord.Hours; ; // Update Hours in Overtime record
                    }

                    // Condition 2: Double Time and Holiday Worked Doubletime
                    var doubleTimeHours = g.Where(r => r.PayType == "Double Time" || r.PayType == "Holiday Worked Doubletime").Sum(r => r.Hours);

                    // Condition 3: Differential with 2SDDT and 2SDHDT
                    var differentialDTAndHDT = g.Where(r => r.PayType == "Differential" && (r.PayRollEarningRole == "2SDDT" || r.PayRollEarningRole == "2SDHDT")).Sum(r => r.Hours);

                    if (doubleTimeHours > 0 && differentialDTAndHDT > 0)
                    {
                        var adjustedDoubleTimeHours = doubleTimeHours - differentialDTAndHDT;

                        // Apply the adjustedDoubleTimeHours back to the Double Time record
                        var doubleTimeRecord = g.FirstOrDefault(r => r.PayType == "Double Time");
                        var differentialDoubleTimeRecord = g.FirstOrDefault(r => r.PayType == "Differential" && (r.PayRollEarningRole == "2SDDT" || r.PayRollEarningRole == "2SDHDT"));
                        if (doubleTimeRecord != null)
                        {
                            doubleTimeRecord.Hours = adjustedDoubleTimeHours;                         

                        }
                        if (differentialDoubleTimeRecord != null)
                        {                            
                            differentialDoubleTimeRecord.Hours = differentialDTAndHDT;
                        }
                    }

                    // Return updated records in the group
                    return g;
                })
                .SelectMany(r => r) // Flatten grouped records
                .ToList();


            // Step 3: Remove specific records based on conditions
            finalRecords = finalRecords
                .GroupBy(r => new { r.EmployeeId, r.Date, r.WorkLocation }) // Group by EmployeeId, Date, and WorkLocation
                .SelectMany(g =>
                {
                    var recordsList = g.ToList(); // Convert grouping to a list for easier manipulation

                    // Condition: Remove "Holiday Worked Doubletime" if both "Double Time" and "Holiday Worked Doubletime" are present
                    var hasDoubleTime = recordsList.Any(r => r.PayType == "Double Time");
                    var holidayDoubleTimeRecord = recordsList.FirstOrDefault(r => r.PayType == "Holiday Worked Doubletime");

                    if (hasDoubleTime && holidayDoubleTimeRecord != null)
                    {
                        // Remove "Holiday Worked Doubletime" record
                        recordsList.Remove(holidayDoubleTimeRecord);
                    }

                    // Condition: Remove "2SDHDT" if both "2SDDT" and "2SDHDT" are present in Differential records
                    var hasDifferentialDT = recordsList.Any(r => r.PayType == "Differential" && r.PayRollEarningRole == "2SDDT");
                    var differentialHDTRecord = recordsList.FirstOrDefault(r => r.PayType == "Differential" && r.PayRollEarningRole == "2SDHDT");

                    if (hasDifferentialDT && differentialHDTRecord != null)
                    {
                        // Remove "2SDHDT" record
                        recordsList.Remove(differentialHDTRecord);
                    }

                    return recordsList; // Return the modified list for this group
                })
                .ToList();


            // Add back the ungrouped "Regular" and "Store Guard" records
            var ungroupedRecords = records
                .Where(r => r.PayType == "Regular" && r.WorkRole == "Store Guard")
                .Select(r => new PayrollRecord
                {
                    EmployeeId = r.EmployeeId,
                    Date = r.Date,
                    PayType = r.PayType,
                    EmployeeName = r.EmployeeName,
                    HomeLocation = r.HomeLocation,
                    JobTitle = r.JobTitle,
                    WorkLocation = r.WorkLocation,
                    WorkRole = r.WorkRole,
                    PayName = r.PayName,
                    PayRollEarningRole = r.PayRollEarningRole,
                    Hours = r.Hours,
                    Rate = r.Rate,
                    Amount = r.Amount
                })
                .ToList();

            // Combine grouped and ungrouped records
            finalRecords.AddRange(ungroupedRecords);
            // Group by Employee Id
            //var groupedRecords = sortedRecords.GroupBy(r => r.EmployeeId);

            foreach (var employeeGroup in finalRecords)
            {
                var lineBuffer = new List<string>();

                //foreach (var record in employeeGroup)
                //{
                var processedLines = await ProcessPayrollLineAsync(employeeGroup);
                if (processedLines != null && processedLines.Any())
                {
                    lineBuffer.AddRange(processedLines);
                }
                //}

                if (lineBuffer.Any())
                {
                    await WriteBatchAsync(destinationFilePath, lineBuffer).ConfigureAwait(false);
                }
            }

            DateTime endTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Finished processing Payroll CSV: {filePath} at {endTime}");
            TimeSpan duration = endTime - startTime;
            LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
        }

        private DateTime ParseDate(string dateStr)
        {
            if (dateStr.Contains("to"))
            {
                var dateParts = dateStr.Split(" to ");
                return DateTime.ParseExact(dateParts[0].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture);
            }
            return DateTime.ParseExact(dateStr.Trim(), "M/d/yyyy", CultureInfo.InvariantCulture);
        }

        private (DateTime startDate, DateTime endDate) ParseDateRange(string dateStr)
        {
            if (dateStr.Contains("to"))
            {
                var dateParts = dateStr.Split(" to ");
                DateTime startDate = DateTime.ParseExact(dateParts[0].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture);
                DateTime endDate = DateTime.ParseExact(dateParts[1].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture);
                return (startDate, endDate);
            }
            DateTime singleDate = DateTime.ParseExact(dateStr.Trim(), "M/d/yyyy", CultureInfo.InvariantCulture);
            return (singleDate, singleDate); // Treat single date as a range with the same start and end date
        }

        public static int DetermineWeek(DateTime startDate)
        {
            // Check if the start date falls in the first or second week of the month
            if (startDate.Day <= 7)
            {
                return 1; // First week
            }
            else
            {
                return 2; // Second week
            }
        }

        private bool IsValidWeeklyDateRange(string dateRange)
        {
            var dateParts = dateRange.Split(" to ");

            if (dateParts.Length != 2) return false; // Ensure there are both start and end dates

            if (!DateTime.TryParseExact(dateParts[0].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime startDate) ||
                !DateTime.TryParseExact(dateParts[1].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime endDate))
            {
                return false; // Invalid date format
            }

            TimeSpan difference = endDate - startDate;

            // Check if the range is 6 days (7 inclusive) and starts on Sunday, ends on Saturday
            return difference.Days == 6 && startDate.DayOfWeek == DayOfWeek.Sunday && endDate.DayOfWeek == DayOfWeek.Saturday;
        }

        private PayrollRecord ParseLineToPayrollRecord(string line)
        {
            try
            {
                var columns = line.Split(',');

                if (columns.Length >= 13)
                {
                    return new PayrollRecord
                    {
                        Date = columns[0].Trim(),
                        EmployeeId = columns[1].Trim(),
                        EmployeeName = columns[2].Trim(),
                        HomeLocation = columns[3].Trim(),
                        JobTitle = columns[4].Trim(),
                        WorkLocation = columns[5].Trim(),
                        WorkRole = columns[6].Trim(),
                        PayType = columns[7].Trim(),
                        PayName = columns[8].Trim(),
                        PayRollEarningRole = columns[9].Trim(),
                        Hours = decimal.Parse(columns[10].Trim()),
                        Rate = decimal.Parse(columns[11].Trim()),
                        Amount = decimal.Parse(columns[12].Trim())
                    };
                }

                LoggerObserver.OnFileFailed($"Malformed line: {line}");
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private async Task<List<string>> ProcessPayrollLineAsync(PayrollRecord record)
        {
            var processedLines = new List<string>();
            string memoCode=string.Empty;
            int? memoAmount = null;
            string specialProcCode = string.Empty;
            DateTime? startDate = null;
            DateTime? endDate = null;
            // Lookup HR data using grouping by location first, then by employeeId
            EmployeeHrData hrData = null;           

            if (employeeHrMapping.ContainsKey(record.EmployeeId))
            {
                var locationGroup = employeeHrMapping[record.EmployeeId];
                hrData = employeeHrMapping.ContainsKey(record.EmployeeId)
                ? employeeHrMapping[record.EmployeeId]
                : null;
            }

            if (hrData == null)
            {
                // Log error if HR data is not found
                LoggerObserver.OnFileFailed($"HR data not found for employee: {record.EmployeeId}");
                return null;
            }

            //if (IsValidWeeklyDateRange(record.Date) & hrData.Hourly & record.Hours >= 0)
            //{
            //    // Extract and parse the start date from the range
            //    startDate = ParseDateRange(record.Date).startDate;
            //    memoCode = "WK";
            //    memoAmount = DetermineWeek(startDate.Value);
            //    startDate = null;
            //}
            //else if (record.Hours < 0)
            //{
            //    specialProcCode = "E";
            //    (startDate, endDate) = ParseDateRange(record.Date);
            //}

            // Lookup pay code based on pay type
            //string earningCode = payCodeMap.ContainsKey(record.PayType) ? payCodeMap[record.PayType] : "E999"; // Default for unknown types
            // Lookup pay code based on pay type in payCodeMap
            //PaycodeData payCodeData = payCodeMap.ContainsKey(record.PayType) ? payCodeMap[record.PayType] : null;


            // Initialize fields with default or empty values
            string regHours = "0.00";
            string otHours = "0.00"; // Overtime hours (if any)         


            // Use default values if pay code is not found
            string earningCode = ""; //payCodeData?.Code ?? "E999"; // Default earning code


            // Lookup company code based on location
            string companyCode = hrData.CompanyId; //Assuming this will be generated based on the file name //companyCodeMap.ContainsKey(hrData.locationName) ? companyCodeMap[hrData.locationName] : "Unknown";
            // Set 2 for salaried employee
            string rateCode = hrData.Salaried ? "2" : "";
            // Determine Temp Dept: Use Work Location if different from Home Location
            string tempDept = record.WorkLocation != record.HomeLocation ? record.WorkLocation : string.Empty;
            // If the pay type is "Regular", assign the hours to Reg Hours
            if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            {
                regHours = record.Hours.ToString("F2"); // Assuming Hours is a decimal and needs to be formatted
            }
            // Initialize variables to hold the values for each column based on PayType mapping
            string regularHoursColumn = "";
            string overtimeHoursColumn = "";
            string otherPayTypeColumn = "";
            string hours3Code = "";
            string earnings3Code = "";


            decimal? regularHours = null;
            decimal? overtimeHours = null;
            decimal? hours3Amount = null;
            decimal? earnings3Amount = null;
            decimal? otherHours = null;

            var payNames = record.PayName.Split('~');
            // Check PayType for "Regular" or "Over Time" using paycodeDict
            if (paycodeDict.ContainsKey(record.PayType))
            {                
                // Filter PaycodeData by both PayType, PayName, and ADPColumn, then remove duplicates
                var filteredPaycodes = paycodeDict[record.PayType]
                .Where(pc =>
                    payNames.Any(pn => string.Equals(pn.Trim(), pc.PayName, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(pc.PayName, record.PayRollEarningRole, StringComparison.OrdinalIgnoreCase)
                )
                .Distinct(new PaycodeDataComparer())
                .ToList();
                // Only proceed if we found matching PaycodeData for both PayType and PayName
                if (filteredPaycodes.Any())
                {
                    // Iterate over all PaycodeData entries in paycodeDataList and create a processed line for each one
                    foreach (var paycodeData in filteredPaycodes)
                    {
                        //var paycodeDataList = paycodeDict[record.PayType];
                        regularHoursColumn = "";
                        overtimeHoursColumn = "";
                        otherPayTypeColumn = "";
                        hours3Code = "";
                        earnings3Code = "";

                        regularHours = null;
                        overtimeHours = null;
                        hours3Amount = null;
                        earnings3Amount = null;
                        otherHours = null;
                        bool isConditionMatched = false; // Flag to check if any condition matches

                        //if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) & paycodeData.ADPColumn.Equals("Reg Hours", StringComparison.OrdinalIgnoreCase))
                        //{
                        //    regularHoursColumn = paycodeData.ADPColumn;
                        //    regularHours = record.Hours;
                        //}
                        if (paycodeData.ADPColumn == "Reg Hours" & payNames.Any(pn => pn.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase)) & record.WorkRole != "Store Guard")
                        {
                            regularHoursColumn = paycodeData.ADPColumn;
                            regularHours = record.Hours;
                            isConditionMatched = true;
                        }
                        //if (record.PayType.Equals("Overtime", StringComparison.OrdinalIgnoreCase))
                        //{
                        //    overtimeHoursColumn = paycodeData.ADPColumn;
                        //    overtimeHours = record.Hours;
                        //}
                        else if (paycodeData.ADPColumn == "O/T Hours" & payNames.Any(pn => pn.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            overtimeHoursColumn = paycodeData.ADPColumn;
                            overtimeHours = record.Hours;
                            isConditionMatched = true;
                        }
                        else if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) & paycodeData.ADPColumn == "Hours 3 Code"
                                & payNames.Any(pn => pn.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                                & record.WorkRole == "Store Guard")
                        {
                            hours3Code = paycodeData.ADPHoursOrAmountCode;
                            hours3Amount = (record.Hours != 0 ? record.Hours : (record.Amount != 0 ? record.Amount : (decimal?)null));
                            isConditionMatched = true;
                        }
                        else if (!record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) & paycodeData.ADPColumn == "Hours 3 Code" & (payNames.Any(pn => pn.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase)) || record.PayRollEarningRole.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            hours3Code = paycodeData.ADPHoursOrAmountCode;
                            hours3Amount = (record.Hours != 0 ? record.Hours : (record.Amount != 0 ? record.Amount : (decimal?)null));
                            isConditionMatched = true;
                        }
                        //else if (paycodeData.ADPColumn == "Hours 3 Amount" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                        //{
                        //    hours3Amount = record.Hours;
                        //    isConditionMatched = true;
                        //} 
                        else if (paycodeData.ADPColumn == "Earnings 3 Code" & payNames.Any(pn => pn.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            earnings3Code = paycodeData.ADPHoursOrAmountCode;
                            earnings3Amount = record.Amount;
                            isConditionMatched = true;
                        }
                        //else if (paycodeData.ADPColumn == "Earnings 3 Amount" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                        //{
                        //    earnings3Amount = record.Amount;
                        //    isConditionMatched = true;
                        //}
                        // Generate processed line for each paycodeData entry
                        if (isConditionMatched)
                        {
                            string processedLine = $"{companyCode},{"Legion"},{record.EmployeeId},{rateCode},{tempDept},{regularHours},{overtimeHours},"
                                                 + $"{hours3Code},{hours3Amount},{earnings3Code},{earnings3Amount},{record.MemoCode},{record.MemoAmount}, {record.SpecialProcCode},"
                                                 + $"{record.OtherStartDate},{record.OtherEndDate}";

                            processedLines.Add(processedLine);
                        }
                    }
                }
                else
                {
                    LoggerObserver.OnFileFailed($"No Paycode found for PayType: {record.PayType} for Employee ID {record.EmployeeId}");
                }
            }

            return processedLines;
        }

        // Custom comparer to eliminate duplicate PaycodeData entries based on PayType, PayName, and ADPColumn
        public class PaycodeDataComparer : IEqualityComparer<PaycodeData>
        {
            public bool Equals(PaycodeData x, PaycodeData y)
            {
                // Define equality based on PayType, PayName, and ADPColumn (you can extend this comparison to other properties as needed)
                return string.Equals(x.PayType, y.PayType, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.PayName, y.PayName, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.ADPColumn, y.ADPColumn, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(PaycodeData obj)
            {
                // Combine the hash codes of PayType, PayName, and ADPColumn
                return (obj.PayType?.ToLower().GetHashCode() ?? 0) ^
                       (obj.PayName?.ToLower().GetHashCode() ?? 0) ^
                       (obj.ADPColumn?.ToLower().GetHashCode() ?? 0);
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
