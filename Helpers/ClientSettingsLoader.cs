using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.Helpers
{
    public static class ClientSettingsLoader
    {
        private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        public static JObject LoadClientSettings(string clientName)
        {
            string json = File.ReadAllText(ConfigFilePath);
            JObject config = JObject.Parse(json);

            foreach (var client in config["Clients"])
            {
                if (client["ClientName"].ToString() == clientName)
                {
                    return (JObject)client;
                }
            }

            throw new Exception("Client not found in the configuration file.");
        }
    }
}
