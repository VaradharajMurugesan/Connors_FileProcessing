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
        ExtractEmployeeEntityData extractEmployeeEntityData = new ExtractEmployeeEntityData();
        private readonly HashSet<string> payrollProcessedFileNumbers;


        public AccrualBalanceExportProcessor(JObject clientSettings)
        {
            var payroll_clientSettings = ClientSettingsLoader.LoadClientSettings("payroll");
            string mappingFilesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());
            string remoteMappingFilePath = clientSettings["Folders"]["remoteEmployeeEntityPath"].ToString();
            string employeeEntityMappingPath = sFTPFileExtract.DownloadAndExtractFile(clientSettings, remoteMappingFilePath, mappingFilesFolderPath, "EmployeeEntity");
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

            try
            {
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
                        try
                        {
                            string fileNumber = csv.GetField<string>("File #");
                            if (!string.IsNullOrWhiteSpace(fileNumber))
                            {
                                fileNumbers.Add(fileNumber);
                            }
                        }
                        catch (CsvHelperException csvEx)
                        {
                            LoggerObserver.Error(csvEx, $"Error reading a row in the payroll file: {latestFile}. Skipping to the next row.");
                        }
                    }
                }
            }
            catch (FileNotFoundException fnfEx)
            {
                LoggerObserver.Error(fnfEx, fnfEx.Message);
                throw; // Re-throw to notify calling code
            }
            catch (UnauthorizedAccessException uaeEx)
            {
                LoggerObserver.Error(uaeEx, $"Access denied to directory or file: {directoryPath}");
                throw; // Re-throw to notify calling code
            }
            catch (IOException ioEx)
            {
                LoggerObserver.Error(ioEx, $"I/O error occurred while accessing the file or directory: {directoryPath}");
                throw; // Re-throw to notify calling code
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, "An unexpected error occurred while loading the processed payroll file.");
                throw; // Re-throw to notify calling code
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

            try
            {
                // Validate if the file exists
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"The file does not exist: {filePath}");
                }

                // Read the CSV file
                using (var reader = new StreamReader(filePath))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    try
                    {
                        csv.Read(); // Read header row
                        csv.ReadHeader();

                        while (csv.Read())
                        {
                            try
                            {
                                var type = csv.GetField<string>("Type");
                                var memoCode = csv.GetField<string>("Memo Code");

                                if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(memoCode))
                                {
                                    accrualMemoCodeMapping[type] = memoCode;
                                }
                            }
                            catch (CsvHelperException csvEx)
                            {
                                LoggerObserver.Error(csvEx, $"Error parsing a row in the CSV file: {filePath}. Skipping to the next row.");
                            }
                        }
                    }
                    catch (CsvHelperException csvEx)
                    {
                        LoggerObserver.Error(csvEx, $"Error initializing or reading the CSV file: {filePath}");
                        throw;
                    }
                }
            }
            catch (FileNotFoundException fnfEx)
            {
                LoggerObserver.Error(fnfEx, fnfEx.Message);
                throw; // Re-throw to notify calling code
            }
            catch (UnauthorizedAccessException uaeEx)
            {
                LoggerObserver.Error(uaeEx, $"Access denied to the file: {filePath}");
                throw; // Re-throw to notify calling code
            }
            catch (IOException ioEx)
            {
                LoggerObserver.Error(ioEx, $"I/O error occurred while accessing the file: {filePath}");
                throw; // Re-throw to notify calling code
            }
            catch (Exception ex)
            {
                LoggerObserver.Error(ex, $"An unexpected error occurred while loading the CSV file: {filePath}");
                throw; // Re-throw to notify calling code
            }

            return accrualMemoCodeMapping;
        }


        public async Task ProcessAsync(string filePath, string destinationPath)
        {

            DateTime startTime = DateTime.Now;
            string timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");
            try
            {
                // Validate if the source file exists
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"The file does not exist: {filePath}");
                }
                

                var records = new List<AccrualBalanceExportData>();
                try
                {
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
                }
                catch (IOException ex)
                {
                    LoggerObserver.Error(ex, $"Error reading the input file: {filePath}");
                    throw;
                }
                catch (InvalidDataException ex)
                {
                    LoggerObserver.Error(ex, ex.Message);
                    throw;
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

                // Extract the first or default CoCode from joinedData
                string companyCode = joinedData.FirstOrDefault()?.CoCode ?? "UNKNOWN";

                string destinationFileName = Path.GetFileName(filePath);
                var destinationFilePath = Path.Combine(destinationPath, $"EPI_{companyCode}_{timestamp}_accruals.csv");

                string header = "Co Code,Batch ID,File #,Rate Code,Temp Dept,Reg Hours,O/T Hours,Hours 3 Code,Hours 3 Amount,Earnings 3 Code,Earnings 3 Amount,Memo Code,Memo Amount,Special Proc Code,Other Begin Date,Other End Date";
                try
                {
                    using (var writer = new StreamWriter(destinationFilePath, false))
                    {
                        await writer.WriteLineAsync(header).ConfigureAwait(false);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    LoggerObserver.Error(ex, $"Access denied while writing to destination file: {destinationFilePath}");
                    throw;
                } 

                foreach (var accrualData in joinedData)
                {
                    try
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
                    catch (Exception ex)
                    {
                        LoggerObserver.Error(ex, $"Error writing data to destination file: {destinationFilePath}");
                        throw;
                    }
                }

                DateTime endTime = DateTime.Now;
                LoggerObserver.LogFileProcessed($"Finished processing Payroll CSV: {filePath} at {endTime}");
                TimeSpan duration = endTime - startTime;
                LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
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
                throw;
            }
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
                LoggerObserver.Error(ex, "Error while parsing the Accrual Export record");
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
