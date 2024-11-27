using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class AccrualBalanceExportData
    {
        public string EmployeeExternalId { get; set; }
        public string LocationExternalId { get; set; }
        public string LocationId { get; set; }
        public string Type { get; set; }       
        public decimal CurrentBalance { get; set; }
        public decimal AvailableBalance { get; set; }
        public decimal Accrued { get; set; }
        public decimal CarryOverBalance { get; set; }
        public decimal? Taken { get; set; }
        public decimal? MemoAmount { get; set; }
        public string MemoCode { get; set; }

    }
}
