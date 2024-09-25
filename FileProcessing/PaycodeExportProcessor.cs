using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.FileProcessing
{
    public class PaycodeExportProcessor : ICsvFileProcessorStrategy
    {
        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            Console.WriteLine($"Processing Paycode Export CSV: {filePath}");
            // Your custom logic for Paycode Export CSV

            // Example logic similar to above
            const int batchSize = 100;
            var lines = File.ReadLines(filePath);
            var batches = lines.Select((line, index) => new { Line = line, Index = index })
                               .GroupBy(x => x.Index / batchSize)
                               .Select(g => g.Select(x => x.Line));

            var tasks = batches.Select(async batch =>
            {
                await ProcessBatchAsync(batch);
            });

            await Task.WhenAll(tasks);
        }

        private async Task ProcessBatchAsync(IEnumerable<string> batch)
        {
            foreach (var line in batch)
            {
                Console.WriteLine($"Processing PaycodeExport line: {line}");
            }
        }
    }

}
