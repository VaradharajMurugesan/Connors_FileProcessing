using CsvHelper;
using DidX.BouncyCastle.Bcpg.Sig;
using ProcessFiles_Demo.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessFiles_Demo.DataModel;
using ProcessFiles_Demo.Helpers;

namespace ProcessFiles_Demo.FileProcessing
{
    public class ExtractLocationEntityData
    {
        public Dictionary<string, LocationEntityData> LoadGroupedLocationMappingFromCsv(string filePath)
        {
            var locationMapping = new Dictionary<string, LocationEntityData>();
            try
            {
                using (var reader = new StreamReader(filePath))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    csv.Read();
                    csv.ReadHeader();

                    while (csv.Read())
                    {
                        string locationExternalId = csv.GetField("LocationExternalId");

                        if (!string.IsNullOrEmpty(locationExternalId))
                        {
                            LocationEntityData locationData = new LocationEntityData
                            {
                                LocationId = csv.GetField("LocationId"), // Parse as Guid
                                LocationExternalId = locationExternalId,
                                LocationName = csv.GetField("LocationName"),
                                LastModifiedDate = DateParser.ParseDateOrDefault(csv.GetField("LastModifiedDate")),
                                LastModifiedByEmployeeId = csv.GetField("LastModifiedByEmployeeId"),
                                LastModifiedByEmployeeExternalId = csv.GetField("LastModifiedByEmployeeExternalId"),
                                Status = csv.GetField("Status"),
                                Contact = csv.GetField("Contact"),
                                Address = csv.GetField("Address"),
                                TimeZone = csv.GetField("TimeZone"),
                                StartOfWeek = csv.GetField("StartOfWeek"),
                                LocationType = csv.GetField("LocationType"),
                                LocationSubType = csv.GetField("LocationSubType"),
                                ParentLocationId = csv.GetField("ParentLocationId"),
                                EffectiveDate = DateParser.ParseDateOrDefault(csv.GetField("EffectiveDate"))
                            };

                            // Add to the dictionary by LocationExternalId
                            if (!locationMapping.ContainsKey(locationExternalId))
                            {
                                locationMapping[locationExternalId] = locationData;
                            }
                        }
                    }
                }
            }
            catch (HeaderValidationException ex)
            {
                // Log error for malformed CSV header
                LoggerObserver.OnFileFailed($"Error: Malformed CSV header in file '{filePath}'. Exception: {ex.Message}");
            }
            catch (FormatException ex)
            {
                // Log error for format issues
                LoggerObserver.OnFileFailed($"Error: Format issue in file '{filePath}'. Exception: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Log any other unexpected errors
                LoggerObserver.OnFileFailed($"Error: An unexpected error occurred while processing the CSV file '{filePath}'. Exception: {ex.Message}");
            }

            // Sort the dictionary by LocationExternalId for performance improvement
            var sortedLocationMapping = new SortedDictionary<string, LocationEntityData>(locationMapping);
            return new Dictionary<string, LocationEntityData>(sortedLocationMapping);
        }

    }
}
