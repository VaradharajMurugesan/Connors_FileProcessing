using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OfficeOpenXml;
using FileTransform.Logging;
using FileTransform.DataModel;
using CsvHelper;
using FileTransform.SFTPExtract;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace FileTransform.FileProcessing
{
    public class PayrollFileProcessor : ICsvFileProcessorStrategy
    {
        // Global list of excluded roles
        private static readonly List<string> ExcludedRoles = new List<string>
        {
            "Store Guard",
            "Store Asset Protection",
            "Remodel",
            "Setup",
            "Inventory",
            "Pre-Open Recruiting",
            "Store Meeting",
            "Training - Manager New Hire",
            "Training - Manager Promotion",
            "Training - Services",
            "Training - Harassment",
            "Training - AP",
            "TSM",
            "Training - Other",
            "Special Project"
        };

        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, EmployeeHrData> employeeHrMapping;
        private Dictionary<string, List<PaycodeData>> paycodeDict;
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();
        ExtractEmployeeEntityData extractEmployeeEntityData = new ExtractEmployeeEntityData();

        public PayrollFileProcessor(JObject clientSettings)
        {
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            string remoteMappingFilePath = "/home/fivebelow-uat/outbox/extracts";
            string employeeEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath, "EmployeeEntity");
            // Load employee HR mapping from Excel (grouped by employee ID now)
            employeeHrMapping = extractEmployeeEntityData.LoadGroupedEmployeeHrMappingFromCsv(employeeEntityMappingPath);
            paycodeDict = LoadPaycodeMappingFromXlsx("LegionPayCodes.xlsx");

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


        public Dictionary<string, List<PaycodeData>> LoadPaycodeMappingFromXlsx(string filePath)
        {
            var paycodeDict = new Dictionary<string, List<PaycodeData>>();

            try
            {
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
                        try
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
                        catch (Exception ex)
                        {
                            // Log or handle row-specific exception
                            LoggerObserver.OnFileFailed($"Error processing PayCode mapping row {row}, {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log or handle file-level exception
                LoggerObserver.OnFileFailed($"Error loading PayCode Mapping Excel file: {ex.Message}");
                throw; // Optionally rethrow the exception if it should propagate
            }

            return paycodeDict;
        }


        // Helper method to determine the memoAmount
        private (string memoCode, int? memoAmount, string specialProcCode, DateTime? otherStartDate, DateTime? otherEndDate) GetMemoAmount(DateTime startDate, DateTime endDate, DateTime fileStartDate, DateTime week1EndDate, DateTime week2StartDate, DateTime fileEndDate)
        {
            if (startDate.Date >= fileStartDate.Date && endDate.Date <= week1EndDate.Date)
            {
                return ("WK", 1, "", null, null); // Week 1
            }
            else if (startDate.Date >= week2StartDate.Date && endDate.Date <= fileEndDate.Date)
            {
                return ("WK", 2, "", null, null); // Week 2
            }
            else
            {
                return ("", null, "E", startDate.Date, endDate.Date);
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

            try
            {


                var records = new List<PayrollRecord>();
                // Read the source file
                try
                {
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
                }
                catch (Exception ex)
                {
                    throw new IOException($"Error reading input file: {filePath}. Details: {ex.Message}", ex);
                }
                // Further processing (sorting, grouping, etc.)
                try
                {
                    // Sort by Employee Id and Date
                    // Step 1: Group records by EmployeeId, date range, PayType, and PayrollEarningRole
                    var initialGroupedRecords = records
                    .Where(r => !(r.PayType == "Regular" && ExcludedRoles.Contains(r.WorkRole))) // Check for multiple roles
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
                                var holidayDoubleTimeRecord = g.FirstOrDefault(r => r.PayType == "Holiday Worked Doubletime");
                                var differentialDoubleTimeRecord = g.FirstOrDefault(r => r.PayType == "Differential" && (r.PayRollEarningRole == "2SDDT" || r.PayRollEarningRole == "2SDHDT"));
                                if (doubleTimeRecord != null)
                                {
                                    doubleTimeRecord.Hours = adjustedDoubleTimeHours;

                                }
                                if (holidayDoubleTimeRecord != null)
                                {
                                    holidayDoubleTimeRecord.Hours = adjustedDoubleTimeHours;

                                }
                                if (differentialDoubleTimeRecord != null)
                                {
                                    differentialDoubleTimeRecord.Hours = differentialDTAndHDT;
                                }
                            }

                            // Exclude records with zero Hours
                            return g.Where(r => r.Hours > 0 || r.Amount > 0);
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
                    .Where(r => r.PayType == "Regular" && ExcludedRoles.Contains(r.WorkRole))
                    .Select(r =>
                    {
                        // Extract the date range from the record's date
                        var dateRange = ParseDateRange(r.Date);

                        // Get memoCode, memoAmount, and other date-based fields based on the date range
                        var (memoCode, memoAmount, specialProcCode, otherStartDate, otherEndDate) =
                            GetMemoAmount(dateRange.startDate, dateRange.endDate, Convert.ToDateTime(fileStartDate), week1EndDate, week2StartDate, Convert.ToDateTime(fileEndDate));

                        return new PayrollRecord
                        {
                            EmployeeId = r.EmployeeId,
                            Date = dateRange.startDate == dateRange.endDate
                                ? dateRange.startDate.ToString("M/d/yyyy")
                                : $"{dateRange.startDate:M/d/yyyy} to {dateRange.endDate:M/d/yyyy}",
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
                            Amount = r.Amount,
                            MemoCode = memoCode,
                            MemoAmount = memoAmount,
                            SpecialProcCode = specialProcCode,
                            OtherStartDate = Convert.ToString(otherStartDate),
                            OtherEndDate = Convert.ToString(otherEndDate),
                        };
                    })
                    .ToList();

                    // Combine grouped and ungrouped records
                    finalRecords.AddRange(ungroupedRecords);
                    // Update RateCode based on SalariedFlag using the existing employeeHRMapping dictionary
                    foreach (var record in finalRecords)
                    {
                        // Check if the EmployeeId exists in the dictionary
                        if (employeeHrMapping.TryGetValue(record.EmployeeId, out var hrData))
                        {
                            // Update RateCode if Salaried is true
                            if (hrData.Salaried)
                            {
                                record.RateCode = 2; // Set RateCode to 2 for salaried employees                                                     
                                record.MemoCode = "";
                                record.MemoAmount = null;
                            }
                            record.CompanyCode = hrData.CompanyId;
                        }
                        else
                        {
                            // Log if EmployeeId is not present in the dictionary
                            LoggerObserver.OnFileFailed($"HR data not found for employee: {record.EmployeeId}");
                        }
                    }

                    // Get the first record from the list
                    var firstRecord = finalRecords.First();

                    // Extract the EmployeeId from the first record
                    var firstEmployeeId = firstRecord.EmployeeId;

                    // Fetch the matching CompanyId from employeeHrMapping
                    string companyCode = employeeHrMapping.TryGetValue(firstEmployeeId, out var employeeHrData)
                                         ? employeeHrData.CompanyId
                                         : "UNKNOWN";
                    var processedLines = finalRecords
                    .Select(record =>
                    {
                        // Initialize fields
                        string memoCode = record.MemoCode;
                        int? memoAmount = record.MemoAmount;
                        string specialProcCode = record.SpecialProcCode;
                        string tempDept = DetermineTempDept(record.HomeLocation, record.WorkLocation);
                        string regHours = "0.00";
                        string otHours = "0.00";
                        string hours3Code = "";
                        string earnings3Code = "";
                        decimal? regularHours = null;
                        decimal? overtimeHours = null;
                        decimal? hours3Amount = null;
                        decimal? earnings3Amount = null;

                        // Pay type logic
                        var payNames = record.PayName.Split('~');
                        if (paycodeDict.ContainsKey(record.PayType))
                        {
                            var filteredPaycodes = paycodeDict[record.PayType]
                                .Where(pc =>
                                    payNames.Any(pn => string.Equals(pn.Trim(), pc.PayName, StringComparison.OrdinalIgnoreCase)) ||
                                    string.Equals(pc.PayName, record.PayRollEarningRole, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(pc.PayName, record.WorkRole, StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(pc => pc.PayName == record.WorkRole)
                                .ThenByDescending(pc => pc.PayName == record.PayRollEarningRole)
                                .FirstOrDefault();

                            if (filteredPaycodes != null)
                            {
                                if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) &&
                                    !ExcludedRoles.Contains(record.PayName.Trim()) &&
                                    filteredPaycodes.ADPColumn == "Reg Hours")
                                {
                                    regularHours = record.Hours;
                                }
                                else if (record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) &&
                                         ExcludedRoles.Contains(record.WorkRole.Trim()))
                                {
                                    if (record.Hours != 0)
                                    {
                                        hours3Code = filteredPaycodes.ADPHoursOrAmountCode;
                                        hours3Amount = record.Hours;
                                    }
                                    else
                                    {
                                        earnings3Code = filteredPaycodes.ADPHoursOrAmountCode;
                                        earnings3Amount = record.Amount;
                                    }
                                }
                                else if (filteredPaycodes.ADPColumn == "O/T Hours" &&
                                         payNames.Any(pn => pn.Equals(filteredPaycodes.PayName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    overtimeHours = record.Hours;
                                }
                                else if (!record.PayType.Equals("Regular", StringComparison.OrdinalIgnoreCase) &&
                                         (payNames.Any(pn => pn.Equals(filteredPaycodes.PayName, StringComparison.OrdinalIgnoreCase)) ||
                                          record.PayRollEarningRole.Equals(filteredPaycodes.PayName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    if (record.Hours != 0)
                                    {
                                        hours3Code = filteredPaycodes.ADPHoursOrAmountCode;
                                        hours3Amount = record.Hours;
                                    }
                                    else
                                    {
                                        earnings3Code = filteredPaycodes.ADPHoursOrAmountCode;
                                        earnings3Amount = record.Amount;
                                    }
                                }

                                // Final line generation
                                return $"{record.CompanyCode},{"Legion"},{record.EmployeeId},{record.RateCode},{tempDept},{regularHours},{overtimeHours}," +
                                       $"{hours3Code},{hours3Amount},{earnings3Code},{earnings3Amount},{memoCode},{memoAmount},{specialProcCode}," +
                                       $"{ExtractDatePart(record.OtherStartDate)},{ExtractDatePart(record.OtherEndDate)}, {record.PayType}";
                            }
                        }
                        LoggerObserver.OnFileFailed($"No Paycode found for PayType: {record.PayType} for Employee ID {record.EmployeeId}");
                        // Return null if no matching paycode is found
                        return null;
                    })
                    .Where(line => !string.IsNullOrEmpty(line)) // Filter out null or empty lines
                    .ToList();

                    // Step 2: Apply Grouping and Summing
                    var groupedLines = processedLines
                        .Select(line => line.Split(',')) // Split each line into fields
                        .GroupBy(fields => new
                        {
                            CoCode = fields[0],
                            BatchId = fields[1],
                            FileNumber = fields[2],
                            RateCode = fields[3],
                            TempDept = fields[4],
                            hours3Code = fields[7],
                            earnings3Code= fields[9],
                            MemoCode = fields[11],
                            MemoAmount = fields[12],
                            SpecialProcCode = fields[13],
                            OtherBeginDate = fields[14],
                            OtherEndDate = fields[15],
                            payType = fields[16],
                        })
                        .Select(group => new
                        {
                            group.Key.CoCode,
                            group.Key.BatchId,
                            group.Key.FileNumber,
                            group.Key.RateCode,
                            group.Key.TempDept,
                            RegHours = group.Sum(fields => decimal.TryParse(fields[5], out var value) ? value : 0),
                            OTHours = group.Sum(fields => decimal.TryParse(fields[6], out var value) ? value : 0),
                            //Hours3Code = group.FirstOrDefault()?.ElementAtOrDefault(7),
                            group.Key.hours3Code,
                            Hours3Amount = group.Sum(fields => decimal.TryParse(fields[8], out var value) ? value : 0),
                            //Earnings3Code = group.FirstOrDefault()?.ElementAtOrDefault(9),
                            group.Key.earnings3Code,
                            Earnings3Amount = group.Sum(fields => decimal.TryParse(fields[10], out var value) ? value : 0),
                            group.Key.MemoCode,
                            group.Key.MemoAmount,
                            group.Key.SpecialProcCode,
                            group.Key.OtherBeginDate,
                            group.Key.OtherEndDate
                        })
                        .ToList();

                    // Step 3: Convert grouped lines to string format with zero value fields set to empty
                    var finalLines = groupedLines.Select(group =>
                    {
                        // Check and replace zero values with empty strings
                        var regHours = group.RegHours == 0 ? "" : group.RegHours.ToString();
                        var otHours = group.OTHours == 0 ? "" : group.OTHours.ToString();
                        var hours3Amount = group.Hours3Amount == 0 ? "" : group.Hours3Amount.ToString();
                        var earnings3Amount = group.Earnings3Amount == 0 ? "" : group.Earnings3Amount.ToString();

                        // Construct the final line
                        return $"{group.CoCode},{group.BatchId},{group.FileNumber},{group.RateCode},{group.TempDept}," +
                               $"{regHours},{otHours}," +
                               $"{group.hours3Code},{hours3Amount}," +
                               $"{group.earnings3Code},{earnings3Amount}," +
                               $"{group.MemoCode},{group.MemoAmount}," +
                               $"{group.SpecialProcCode},{group.OtherBeginDate},{group.OtherEndDate}";
                    }).ToList();


                    string destinationFileName = Path.GetFileName(filePath);
                    var destinationFilePath = Path.Combine(destinationPath, $"EPI_{companyCode}_{timestamp}_payfile.csv");

                    string header = "Co Code,Batch ID,File #,Rate Code,Temp Dept,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount,Earnings 3 Code,Earnings 3 Amount,Memo Code,Memo Amount,Special Proc Code,Other Begin Date,Other End Date";

                    // Validate input paths
                    if (!File.Exists(filePath))
                    {
                        throw new FileNotFoundException($"Input file not found: {filePath}");
                    }

                    if (!Directory.Exists(destinationPath))
                    {
                        throw new DirectoryNotFoundException($"Destination path does not exist: {destinationPath}");
                    }

                    // Write the processed lines to the destination file
                    try
                    {
                        using (var writer = new StreamWriter(destinationFilePath, false))
                        {
                            await writer.WriteLineAsync(header).ConfigureAwait(false);
                            foreach (var line in finalLines)
                            {
                                await writer.WriteLineAsync(line).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        throw new IOException($"Error writing to destination file: {destinationFilePath}.", ex);
                    } 

                }
                catch (Exception ex)
                {
                    LoggerObserver.OnFileFailed($"Error processing payroll data. Details: {ex.Message}");
                    throw;
                }

                DateTime endTime = DateTime.Now;
                LoggerObserver.LogFileProcessed($"Finished processing Payroll CSV: {filePath} at {endTime}");
                TimeSpan duration = endTime - startTime;
                LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"Critical error during processing Payroll CSV: {filePath}");
                throw; // Rethrow for higher-level handling if needed
            }
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
                LoggerObserver.Error(ex, $"Exception occure on line: {line}");
                return null;
            }
        }

        public static string DetermineTempDept(string homeLocation, string workLocation)
        {
            // Initialize TempDept as empty
            string tempDept = string.Empty;

            // Ensure HomeLocation and WorkLocation are numeric
            bool isHomeLocationNumeric = int.TryParse(homeLocation, out int homeLocationValue);
            bool isWorkLocationNumeric = int.TryParse(workLocation.Split('_')[0], out int workLocationValue);

            if (isHomeLocationNumeric && isWorkLocationNumeric)
            {
                if (homeLocationValue == workLocationValue)
                {
                    // Case 1: Both locations are the same, set TempDept as empty
                    tempDept = string.Empty;
                }
                else
                {
                    // Case 3: Locations are numeric but different, set TempDept as WorkLocation
                    tempDept = workLocation;
                }
            }
            else
            {
                // Handle non-numeric WorkLocation scenarios
                if (isHomeLocationNumeric && workLocation.Contains('_'))
                {
                    // Case 2: Check for match after splitting WorkLocation with '_'
                    string[] workLocationParts = workLocation.Split('_');
                    if (int.TryParse(workLocationParts[0], out int splitWorkLocationValue) && homeLocationValue == splitWorkLocationValue)
                    {
                        tempDept = string.Empty; // Matched after split
                    }
                    else
                    {
                        tempDept = workLocation; // Different even after split
                    }
                }
                else
                {
                    // Case 4: Non-numeric locations like 'EasterTime', '1abcd_ET', set TempDept as empty
                    tempDept = string.Empty;
                }
            }

            return tempDept;
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

        // Helper method to extract only the date part from a string if it represents a DateTime
        private string ExtractDatePart(string dateStr)
        {
            return DateTime.TryParse(dateStr, out DateTime date)
                ? date.ToString("M/d/yyyy")
                : dateStr;
        }
       
    }
}
