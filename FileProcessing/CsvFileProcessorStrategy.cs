using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.FileProcessing
{
    public class CsvFileProcessorStrategy
    {
        public async Task ProcessAsync(string filePath, string destinationPath)
        {
            // Use the factory to get the appropriate processor for the given file
            var processor = CsvFileProcessorFactory.GetProcessor(Path.GetFileName(filePath));

            // Delegate the processing to the correct strategy
            await processor.ProcessAsync(filePath, destinationPath);
        }
    }

}
