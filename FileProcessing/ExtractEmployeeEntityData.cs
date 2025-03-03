using CsvHelper;
using FileTransform.DataModel;
using FileTransform.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.FileProcessing
{
    internal class ExtractEmployeeEntityData
    {
        // Optimized method to load and group employee HR data by EmployeeExternalId from CSV
        public Dictionary<string, EmployeeHrData> LoadGroupedEmployeeHrMappingFromCsv(string filePath)
        {
            var hrMapping = new Dictionary<string, EmployeeHrData>();
            try
            {
                using (var reader = new StreamReader(filePath))
                using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
                {
                    csv.Read();
                    csv.ReadHeader();

                    while (csv.Read())
                    {
                        string employeeExternalId = csv.GetField("EmployeeExternalId");

                        if (!string.IsNullOrEmpty(employeeExternalId))
                        {
                            EmployeeHrData hrData = new EmployeeHrData
                            {
                                EmployeeId = csv.GetField("EmployeeId"),
                                EmployeeExternalId = employeeExternalId,
                                LegionUserId = Convert.ToInt64(csv.GetField("LegionUserId")),
                                LocationId = csv.GetField("LocationId"),
                                LocationExternalId = csv.GetField("LocationExternalId"),
                                LastModifiedDate = DateTime.Parse(csv.GetField("LastModifiedDate")),
                                FirstName = csv.GetField("FirstName"),
                                LastName = csv.GetField("LastName"),
                                MiddleInitial = csv.GetField("MiddleInitial"),
                                NickName = csv.GetField("NickName"),
                                Title = csv.GetField("Title"),
                                Email = csv.GetField("Email"),
                                PhoneNumber = csv.GetField("PhoneNumber"),
                                Status = csv.GetField("Status"),
                                ManagerId = csv.GetField("ManagerId"),
                                Salaried = csv.GetField<bool>("Salaried"),
                                Hourly = csv.GetField<bool>("Hourly"),
                                Exempt = csv.GetField<bool>("Exempt"),
                                HourlyRate = decimal.TryParse(csv.GetField("HourlyRate"), out var hourlyRate) ? hourlyRate : 0,
                                LegionUserFirstName = csv.GetField("LegionUserFirstName"),
                                LegionUserLastName = csv.GetField("LegionUserLastName"),
                                LegionUserNickName = csv.GetField("LegionUserNickName"),
                                LegionUserEmail = csv.GetField("LegionUserEmail"),
                                LegionUserPhoneNumber = csv.GetField("LegionUserPhoneNumber"),
                                LegionUserAddress = csv.GetField("LegionUserAddress"),
                                LegionUserPhoto = csv.GetField("LegionUserPhoto"),
                                LegionUserBusinessPhoto = csv.GetField("LegionUserBusinessPhoto"),
                                CompanyId = csv.GetField("CompanyId"),
                                CompanyName = csv.GetField("CompanyName")
                            };

                            // Add to the dictionary by EmployeeExternalId
                            if (!hrMapping.ContainsKey(employeeExternalId))
                            {
                                hrMapping[employeeExternalId] = hrData;
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

            // Sort the dictionary by EmployeeExternalId for performance improvement
            var sortedHrMapping = new SortedDictionary<string, EmployeeHrData>(hrMapping);
            return new Dictionary<string, EmployeeHrData>(sortedHrMapping);
        }
    }
}
