using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.FileProcessing
{
    public interface ICsvFileProcessorStrategy
    {
        Task ProcessAsync(string filePath, string destinationPath);
    }

}
