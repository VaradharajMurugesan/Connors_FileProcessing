using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class PayrollRecord
    {
        public DateTime Date { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeName { get; set; }
        public string HomeLocation { get; set; }
        public string JobTitle { get; set; }
        public string WorkLocation { get; set; }
        public string WorkRole { get; set; }
        public string PayType { get; set; }
        public decimal Hours { get; set; }
        public string TimesheetId { get; set; }
    }
}
