using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileTransform.FileProcessing;
using FileTransform.Logging;

namespace FileTransform.Commands
{
    public interface ICommand
    {
        Task ExecuteAsync();
    }

    public class ProcessFileCommand : ICommand
    {
        private readonly ICsvFileProcessorStrategy _processor;
        private readonly string _filePath;
        private readonly string _destinationPath;

        public ProcessFileCommand(ICsvFileProcessorStrategy processor, string filePath, string destinationPath)
        {
            _processor = processor;
            _filePath = filePath;
            _destinationPath = destinationPath;
        }

        public async Task ExecuteAsync()
        {
            await _processor.ProcessAsync(_filePath, _destinationPath);
            LoggerObserver.LogFileProcessed(_filePath);
        }
    }
}
