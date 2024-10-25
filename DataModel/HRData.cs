using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    // HR Data Model
    public class EmployeeHrData
    {
        public string address1 { get; set; }
        public string city { get; set; }
        public string costCenter { get; set; }
        public string email { get; set; }
        public bool exempt { get; set; }
        public string externalId { get; set; }
        public string firstName { get; set; }
        public string hireDate { get; set; }
        public bool hourly { get; set; }
        public string hourlyRate { get; set; }
        public string jobTitle { get; set; }
        public string lastName { get; set; }
        public string locationName { get; set; }
        public bool salaried { get; set; }
        public string state { get; set; }
        public string zip { get; set; }
    }
}
