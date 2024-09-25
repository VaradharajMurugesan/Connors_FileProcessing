using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.FileProcessing
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
