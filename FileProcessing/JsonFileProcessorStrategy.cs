using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace FileTransform.FileProcessing
{
    public class JsonFileProcessorStrategy : ICsvFileProcessorStrategy
    {
        public async Task ProcessAsync(string jsonData, string destinationPath)
        {
            dynamic json = JsonConvert.DeserializeObject(jsonData);
            // Process JSON (add your logic here)
            Console.WriteLine(json.ToString());
        }
    }
}
