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
using System.Text;
using ProcessFiles_Demo.Helpers;

namespace ProcessFiles_Demo.FileProcessing
{
    public class AccrualBalanceExportProcessor : ICsvFileProcessorStrategy
    {
        // Grouped HR mapping: Dictionary maps employeeId -> EmployeeHrData
        private Dictionary<string, EmployeeHrData> employeeHrMapping;
        private Dictionary<string, string> accrualMemoCodeMapping;
        private Dictionary<string, List<PaycodeData>> paycodeDict;
        SFTPFileExtract sFTPFileExtract = new SFTPFileExtract();
        ExtractEmployeeEntityData extractEmployeeEntityData= new ExtractEmployeeEntityData();
        private readonly HashSet<string> payrollProcessedFileNumbers;


        public AccrualBalanceExportProcessor(JObject clientSettings)
        {
            var payroll_clientSettings = ClientSettingsLoader.LoadClientSettings("payroll");
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            string remoteMappingFilePath = clientSettings["Folders"]["remoteEmployeeEntityPath"].ToString(); 
            string employeeEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath);
            // Load employee HR mapping from Excel (grouped by employee ID now)
            employeeHrMapping = extractEmployeeEntityData.LoadGroupedEmployeeHrMappingFromCsv(employeeEntityMappingPath);
            accrualMemoCodeMapping = LoadAccrualMemoCodeMappingFromCSV("AccrualMemoCodeMapping.csv");
            string payrollFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, payroll_clientSettings["Folders"]["outputFolder"].ToString());
            payrollProcessedFileNumbers = LoadProcessedPayRollFile(payrollFilePath);

        }

        /// <summary>
        /// Loads processed payroll file by finding the latest file in the directory and extracting "File #" values.
        /// </summary>
        /// <param name="directoryPath">The directory containing payroll files.</param>
        /// <returns>A HashSet containing the extracted "File #" values from the latest file.</returns>
        public static HashSet<string> LoadProcessedPayRollFile(string directoryPath)
        {
            var fileNumbers = new HashSet<string>();

            // Get the latest file from the directory
            var latestFile = GetLatestFile(directoryPath);
            if (latestFile == null)
            {
                throw new FileNotFoundException($"No files found in the specified directory: {directoryPath}");
            }

            // Read the latest file and extract "File #" values
            using (var reader = new StreamReader(latestFile))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                csv.Read(); // Read the header row
                csv.ReadHeader();

                while (csv.Read())
                {
                    string fileNumber = csv.GetField<string>("File #");
                    if (!string.IsNullOrWhiteSpace(fileNumber))
                    {
                        fileNumbers.Add(fileNumber);
                    }
                }
            }

            return fileNumbers;
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
        public Dictionary<string, string> LoadAccrualMemoCodeMappingFromCSV(string filePath)
        {
            var accrualMemoCodeMapping = new Dictionary<string, string>();

            using (var reader = new StreamReader(filePath))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                csv.Read(); // Read header row
                csv.ReadHeader();

                while (csv.Read())
                {
                    var type = csv.GetField<string>("Type");
                    var memoCode = csv.GetField<string>("Memo Code");

                    if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(memoCode))
                    {
                        accrualMemoCodeMapping[type] = memoCode;
                    }
                }
            }

            return accrualMemoCodeMapping;
        }

        public async Task ProcessAsync(string filePath, string destinationPath)
        {                

            DateTime startTime = DateTime.Now;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");

            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"AccrualBalanceExport_{timestamp}.csv");

            string header = "Co Code,Batch ID,File #,Rate Code,Temp Dept,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount,Earnings 3 Code,Earnings 3 Amount,Memo Code,Memo Amount,Special Proc Code,Other Begin Date,Other End Date";
            using (var writer = new StreamWriter(destinationFilePath, false))
            {
                await writer.WriteLineAsync(header).ConfigureAwait(false);
            }

            var records = new List<AccrualBalanceExportData>();

            using (var reader = new StreamReader(filePath, Encoding.UTF8))
            {
                // Read and skip the header line
                string headerLine = await reader.ReadLineAsync().ConfigureAwait(false);

                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var accrualBalanceExportRecord = ParseLineToAccrualExportRecord(line);
                    if (accrualBalanceExportRecord != null)
                    {
                        records.Add(accrualBalanceExportRecord);
                    }
                    else
                    {
                        LoggerObserver.OnFileFailed($"Malformed line: {line}");
                    }
                }
            }


            // Perform the inner join operation on employeeId and type
            var joinedData = records
                .Where(record =>
                    employeeHrMapping.ContainsKey(record.EmployeeExternalId) && // Inner join on EmployeeExternalId
                    accrualMemoCodeMapping.ContainsKey(record.Type) && // Inner join on Type
                    payrollProcessedFileNumbers.Contains(record.EmployeeExternalId)) // Inner join on File #
                .SelectMany(record =>
                {
                    // Fields from records
                    var companyId = employeeHrMapping[record.EmployeeExternalId].CompanyId;
                    var isSalaried = employeeHrMapping[record.EmployeeExternalId].Salaried;
                    var memoCode = accrualMemoCodeMapping[record.Type]; // MemoCode is guaranteed to exist because of the inner join condition

                    // Calculate the MemoAmount
                    var memoAmount = record.CurrentBalance; // Adjust logic if needed

                    // Create the primary record
                    var result = new List<dynamic>
                    {
                        new
                        {
                            CoCode = companyId,
                            BatchID = "Accrual",
                            EmployeeExternalId = record.EmployeeExternalId,
                            FileNo = record.EmployeeExternalId,
                            RateCode = isSalaried ? "2" : "",
                            MemoCode = memoCode,
                            MemoAmount = memoAmount
                        }
                    };

                    // Check if MemoCode is "SCK" and add a duplicate record with modified values
                    if (memoCode == "SCK")
                    {
                        result.Add(new
                        {
                            CoCode = companyId,
                            BatchID = "Accrual",
                            EmployeeExternalId = record.EmployeeExternalId,
                            FileNo = record.EmployeeExternalId,
                            RateCode = isSalaried ? "2" : "",
                            MemoCode = "ACC", // New MemoCode
                            MemoAmount = record.Accrued, // New MemoAmount from Accrued
                        });
                    }

                    return result;
                })
                .ToList();

            foreach (var accrualData in joinedData)
            {
                var lineBuffer = new List<string>();

                //foreach (var record in employeeGroup)
                //{
                //var processedLines = await ProcessPayrollLineAsync(employeeGroup);
                string processedLine = $"{accrualData.CoCode},{accrualData.BatchID},{accrualData.FileNo},{accrualData.RateCode}," 
                                        + $"{""},{""},{""},{""},{""},{""},{""},{accrualData.MemoCode},{accrualData.MemoAmount}, {""},"
                                        + $"{""},{""}";
                
                lineBuffer.Add(processedLine);
               

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

       

        private AccrualBalanceExportData ParseLineToAccrualExportRecord(string line)
        {
            try
            {
                var columns = line.Split(',');

                if (columns.Length >= 9)
                {
                    return new AccrualBalanceExportData
                    {                        
                        EmployeeExternalId = columns[0].Trim(),
                        LocationId = columns[1].Trim(),
                        LocationExternalId = columns[2].Trim(),                        
                        Type = columns[3].Trim(),
                        CurrentBalance = decimal.Parse(columns[4].Trim()),
                        AvailableBalance = decimal.Parse(columns[5].Trim()),
                        Accrued = decimal.Parse(columns[6].Trim()),
                        CarryOverBalance = decimal.Parse(columns[7].Trim()),
                        Taken = decimal.Parse(columns[8].Trim())
                        
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
