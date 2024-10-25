using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class PaycodeData
    {
        public string PayType { get; set; } // Legion Pay Type
        public string PayName { get; set; } // Legion Pay Name
        public string Reference { get; set; } // Reference
        public string ADPColumn { get; set; } // ADP Column
        public string ADPHoursOrAmountCode { get; set; } // ADP Hours or Amount Code
        public string PassForHourly { get; set; } // Pass Yes or No (Hourly)
        public string PassForSalary { get; set; } // Pass Yes or No (Salary)       
    }

    // CsvHelper ClassMap for PaycodeData to handle header differences
    //public sealed class PaycodeDataMap : ClassMap<PaycodeData>
    //{
    //    public PaycodeDataMap()
    //    {
    //        Map(m => m.KronosPayCodeID).Name("Kronos Pay Code ID");
    //        Map(m => m.KronosPayCode).Name("Kronos Pay Code");
    //        Map(m => m.ADPHoursType).Name("ADP Hours Type");
    //        Map(m => m.ADPHoursCode).Name("ADP Hours Code");
    //        Map(m => m.PassYesOrNoHourly).Name("Pass Yes or No Hourly");
    //        Map(m => m.PassYesOrNoSalary).Name("Pass Yes or No Salary");
    //        Map(m => m.Code).Name("Code");
    //        Map(m => m.Description).Name("Description");
    //        Map(m => m.ShortDescription).Name("Short Description");
    //        Map(m => m.EarningsType).Name("Earnings Type");
    //        Map(m => m.HoursField).Name("Hours Field");
    //        Map(m => m.EarningsField).Name("Earnings Field");
    //    }
    //}

}
