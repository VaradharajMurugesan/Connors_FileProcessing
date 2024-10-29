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

namespace ProcessFiles_Demo.FileProcessing
{
    public class PayrollFileProcessor : ICsvFileProcessorStrategy
    {
        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, EmployeeHrData> employeeHrMapping;        
        private Dictionary<string, PaycodeData> payCodeMap; // Updated type
        private Dictionary<string, List<PaycodeData>> paycodeDict;

        public PayrollFileProcessor()
        {
            // Load employee HR mapping from Excel (grouped by employee ID now)
            employeeHrMapping = LoadGroupedEmployeeHrMappingFromCsv("EmployeeEntity-2024-287-2024-321.csv");
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
                            HourlyRate = Convert.ToDecimal(csv.GetField("HourlyRate")),
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

        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");

            const int batchSize = 1000;
            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"Payroll_{timestamp}.csv");

            var lineBuffer = new List<string>(batchSize);
            // Write the header to the file before processing the data
            string header = "Co Code,Batch ID,File #,Rate Code,Temp Dept,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount,Earnings 3 Code,Earnings 3 Amount,Memo Code,Memo Amount,Special Proc Code,Other Begin Date,Other End Date";
            using (var writer = new StreamWriter(destinationFilePath, false)) // Overwrite mode to ensure we start fresh
            {
                await writer.WriteLineAsync(header).ConfigureAwait(false); // Write the header
            }

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
                        var processedLines = await ProcessPayrollLineAsync(payrollRecord);
                        if (processedLines != null && processedLines.Any())
                        {
                            lineBuffer.AddRange(processedLines); // Add all processed lines for the current record
                        }

                        if (lineBuffer.Count > 0 && lineBuffer.Count % batchSize == 0)
                        {
                            await WriteBatchAsync(destinationFilePath, lineBuffer).ConfigureAwait(false);
                            lineBuffer.Clear(); // Clear buffer after writing
                        }
                    }
                    else
                    {
                        LoggerObserver.OnFileFailed($"Malformed line: {line}");
                    }
                }
            }

            // Write any remaining lines
            if (lineBuffer.Any())
            {
                await WriteBatchAsync(destinationFilePath, lineBuffer).ConfigureAwait(false);
            }

            DateTime endTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Finished processing Payroll CSV: {filePath} at {endTime}");
            TimeSpan duration = endTime - startTime;
            LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
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


            decimal regularHours = 0;
            decimal overtimeHours = 0;
            decimal hours3Amount = 0;
            decimal earnings3Amount = 0;
            decimal otherHours = 0;

            // Check PayType for "Regular" or "Over Time" using paycodeDict
            if (paycodeDict.ContainsKey(record.PayType))
            {
                // Filter PaycodeData by both PayType, PayName, and ADPColumn, then remove duplicates
                var filteredPaycodes = paycodeDict[record.PayType]
                    .Where(pc => string.Equals(pc.PayName, record.PayName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(new PaycodeDataComparer()) // Use Distinct with custom comparer
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

                        regularHours = 0;
                        overtimeHours = 0;
                        hours3Amount = 0;
                        earnings3Amount = 0;
                        otherHours = 0;
                        bool isConditionMatched = false; // Flag to check if any condition matches

                        //if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) & paycodeData.ADPColumn.Equals("Reg Hours", StringComparison.OrdinalIgnoreCase))
                        //{
                        //    regularHoursColumn = paycodeData.ADPColumn;
                        //    regularHours = record.Hours;
                        //}
                        if (paycodeData.ADPColumn == "Reg Hours" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
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
                        else if (paycodeData.ADPColumn == "O/T Hours" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                        {
                            overtimeHoursColumn = paycodeData.ADPColumn;
                            overtimeHours = record.Hours;
                            isConditionMatched = true;
                        }
                        else if (paycodeData.ADPColumn == "Hours 3 Code" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                        {
                            hours3Code = paycodeData.ADPHoursOrAmountCode;
                            hours3Amount = record.Hours;
                            isConditionMatched = true;
                        }
                        //else if (paycodeData.ADPColumn == "Hours 3 Amount" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
                        //{
                        //    hours3Amount = record.Hours;
                        //    isConditionMatched = true;
                        //} 
                        else if (paycodeData.ADPColumn == "Earnings 3 Code" & record.PayName.Equals(paycodeData.PayName, StringComparison.OrdinalIgnoreCase))
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
                                                 + $"{hours3Code},{hours3Amount},{earnings3Code},{earnings3Amount},{"memoCode"}";

                            processedLines.Add(processedLine);
                        }
                    }
                }
                else
                {
                    LoggerObserver.OnFileFailed($"No Paycode found for PayType: {record.PayType}");
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
