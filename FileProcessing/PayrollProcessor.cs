using System;
using System.IO;
using System.Threading.Tasks;
using ProcessFiles_Demo.Logging;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PayrollProcessor : ICsvFileProcessorStrategy
    {
        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            DateTime startTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Start processing Payroll CSV: {filePath} at {startTime}");

            string destinationFileName = Path.GetFileName(filePath);
            var destinationFilePath = Path.Combine(destinationPath, $"Processed_{destinationFileName}");

            // Example business logic to process payroll CSV
            using (var reader = new StreamReader(filePath))
            using (var writer = new StreamWriter(destinationFilePath))
            {
                // Write a custom header for the payroll CSV
                await writer.WriteLineAsync("Employee ID,Salary,Bonuses,Deductions,Net Pay").ConfigureAwait(false);

                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    // Process each line (add your payroll-specific business logic here)
                    var processedLine = ProcessPayrollLine(line);
                    await writer.WriteLineAsync(processedLine).ConfigureAwait(false);
                }
            }

            DateTime endTime = DateTime.Now;
            LoggerObserver.LogFileProcessed($"Finished processing Payroll CSV: {filePath} at {endTime}");
            TimeSpan duration = endTime - startTime;
            LoggerObserver.LogFileProcessed($"Time taken to process file: {duration.TotalSeconds} seconds.");
        }

        private string ProcessPayrollLine(string line)
        {
            // Logic to process each line in the payroll CSV
            // Split line, apply transformations, map data, etc.
            // For example:
            var columns = line.Split(',');
            if (columns.Length < 5)
            {
                LoggerObserver.OnFileFailed($"Malformed payroll line: {line}");
                return null;
            }

            // You can manipulate the columns as needed for payroll
            return $"{columns[0]},{columns[1]},{columns[2]},{columns[3]},{columns[4]}"; // Adjust as per payroll format
        }
    }
}
