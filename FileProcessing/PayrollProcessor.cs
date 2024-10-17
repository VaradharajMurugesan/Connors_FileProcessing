using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OfficeOpenXml;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.DataModel;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PayrollFileProcessor : ICsvFileProcessorStrategy
    {
        // Grouped HR mapping: Outer dictionary maps location -> inner dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, Dictionary<string, EmployeeHrData>> groupedEmployeeHrMapping;
        private Dictionary<string, string> companyCodeMap;
        private Dictionary<string, string> payCodeMap;

        public PayrollFileProcessor()
        {
            // Sample company code mapping
            companyCodeMap = new Dictionary<string, string>()
            {
                { "Location_A", "Company_001" },
                { "Location_B", "Company_002" },
                { "100", "Company_003" }
            };

            // Load employee HR mapping from Excel
            groupedEmployeeHrMapping = LoadGroupedEmployeeHrMappingFromExcel("Employee_HR_mapping.xlsx");

            // Sample pay code mapping (map earning codes to pay types)
            payCodeMap = new Dictionary<string, string>()
            {
                { "Regular Hours", "E100" },
                { "Overtime", "E200" },
                { "Holiday Pay", "E300" },
                { "Sick Leave", "E400" }
            };
        }

        // Optimized method to load and group employee HR data by location
        private Dictionary<string, Dictionary<string, EmployeeHrData>> LoadGroupedEmployeeHrMappingFromExcel(string filePath)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            var groupedHrMapping = new Dictionary<string, Dictionary<string, EmployeeHrData>>();

            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                ExcelWorksheet worksheet = package.Workbook.Worksheets[0]; // Assuming data is on the first sheet
                int rowCount = worksheet.Dimension.Rows;

                // Assuming the first row is the header and data starts from row 2
                for (int row = 2; row <= rowCount; row++)
                {
                    string employeeId = worksheet.Cells[row, 1].Value?.ToString().Trim(); // Assuming employee ID is in the first column
                    string locationName = worksheet.Cells[row, 4].Value?.ToString().Trim(); // Assuming location in column 4

                    if (!string.IsNullOrEmpty(employeeId) && !string.IsNullOrEmpty(locationName))
                    {
                        EmployeeHrData hrData = new EmployeeHrData
                        {
                            externalId = employeeId,
                            firstName = worksheet.Cells[row, 2].Value?.ToString().Trim(),    // Assuming first name in column 2
                            lastName = worksheet.Cells[row, 3].Value?.ToString().Trim(),     // Assuming last name in column 3
                            locationName = locationName,
                            jobTitle = worksheet.Cells[row, 5].Value?.ToString().Trim(),     // Assuming job title in column 5
                            hourlyRate = worksheet.Cells[row, 6].Value?.ToString().Trim(),    // Assuming hourly rate in column 6
                            salaried = Convert.ToBoolean(worksheet.Cells[row, 7].Value?.ToString().Trim().ToLower())
                        };

                        // Add to the grouped dictionary by location and employeeId
                        if (!groupedHrMapping.ContainsKey(locationName))
                        {
                            groupedHrMapping[locationName] = new Dictionary<string, EmployeeHrData>();
                        }

                        groupedHrMapping[locationName][employeeId] = hrData;
                    }
                }
            }

            return groupedHrMapping;
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
                        string processedLine = await ProcessPayrollLineAsync(payrollRecord);
                        if (processedLine != null)
                        {
                            lineBuffer.Add(processedLine);
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
            var columns = line.Split(',');

            if (columns.Length >= 14)
            {
                return new PayrollRecord
                {
                    Date = DateTime.Parse(columns[0].Trim()),
                    EmployeeId = columns[1].Trim(),
                    EmployeeName = columns[2].Trim(),
                    HomeLocation = columns[3].Trim(),
                    JobTitle = columns[4].Trim(),
                    WorkLocation = columns[5].Trim(),
                    WorkRole = columns[6].Trim(),
                    PayType = columns[7].Trim(),
                    Hours = decimal.Parse(columns[10].Trim()),
                    TimesheetId = columns[13].Trim()
                };
            }

            LoggerObserver.OnFileFailed($"Malformed line: {line}");
            return null;
        }

        private async Task<string> ProcessPayrollLineAsync(PayrollRecord record)
        {
            // Lookup HR data using grouping by location first, then by employeeId
            EmployeeHrData hrData = null;
            if (groupedEmployeeHrMapping.ContainsKey(record.WorkLocation))
            {
                var locationGroup = groupedEmployeeHrMapping[record.WorkLocation];
                hrData = locationGroup.ContainsKey(record.EmployeeId) ? locationGroup[record.EmployeeId] : null;
            }

            if (hrData == null)
            {
                // Log error if HR data is not found
                LoggerObserver.OnFileFailed($"HR data not found for employee: {record.EmployeeId}");
                return null;
            }

            // Lookup pay code based on pay type
            string earningCode = payCodeMap.ContainsKey(record.PayType) ? payCodeMap[record.PayType] : "E999"; // Default for unknown types
            // Lookup company code based on location
            string companyCode = companyCodeMap.ContainsKey(hrData.locationName) ? companyCodeMap[hrData.locationName] : "Unknown";
            // Set 2 for salaried employee
            string rateCode = hrData.salaried ? "2" : "";
            // Determine Temp Dept: Use Work Location if different from Home Location
            string tempDept = record.WorkLocation != record.HomeLocation ? record.WorkLocation : string.Empty;
            // Populate the payroll line using HR data and calculated values
            string processedLine = $"{companyCode},{"Legion"},{record.EmployeeId},{rateCode},{tempDept},{hrData.jobTitle},{record.Hours},{earningCode},{record.PayType},{hrData.hourlyRate}";
            return processedLine;
        }

        private async Task WriteBatchAsync(string filePath, List<string> lines)
        {
            using (var writer = new StreamWriter(filePath, true)) // Append mode
            {
                foreach (var line in lines)
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }
    }

    // HR Data Model
    public class EmployeeHrData
    {
        public string address1 { get; set; }
        public string city { get; set; }
        public string costCenter { get; set; }
        public string email { get; set; }
        public bool exempt { get; set; }
        public string externalId { get; set; }
        public string firstName { get; set; }
        public string hireDate { get; set; }
        public bool hourly { get; set; }
        public string hourlyRate { get; set; }
        public string jobTitle { get; set; }
        public string lastName { get; set; }
        public string locationName { get; set; }
        public bool salaried { get; set; }
        public string state { get; set; }
        public string zip { get; set; }
    }
}
