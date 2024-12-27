using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class PayrollRecord
    {
        public string Date { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeName { get; set; }
        public string HomeLocation { get; set; }
        public string JobTitle { get; set; }
        public string WorkLocation { get; set; }
        public string WorkRole { get; set; }
        public string PayType { get; set; }
        public string PayName { get; set; }
        public string PayRollEarningRole { get; set; }
        public decimal Hours { get; set; }
        public decimal Rate { get; set; }
        public decimal Amount { get; set; }
        public string TimesheetId { get; set; }
        public int? MemoAmount { get; set; }
        public string MemoCode { get; set; }
        public string SpecialProcCode { get; set; }
        public string OtherStartDate { get; set; }
        public string OtherEndDate { get; set; }
        public decimal? RateCode { get; set; }
        public string CompanyCode { get; set; }
    }
}
