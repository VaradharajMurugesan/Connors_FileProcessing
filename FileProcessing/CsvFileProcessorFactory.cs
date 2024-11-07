﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.FileProcessing
{
    public static class CsvFileProcessorFactory
    {
        public static ICsvFileProcessorStrategy GetProcessor(string processortype, JObject clientSettings)
        {
            // Determine the type of file and return the appropriate processor
            if (processortype.Contains("punchexport", StringComparison.OrdinalIgnoreCase))
            {
                return new PunchExportProcessor();
            }
            else if (processortype.Contains("paycodeexport", StringComparison.OrdinalIgnoreCase))
            {
                return new PaycodeExportProcessor();
            }
            else if (processortype.Contains("payroll", StringComparison.OrdinalIgnoreCase))
            {
                return new PayrollFileProcessor(clientSettings);
            }
            else
            {
                throw new ArgumentException("Unknown CSV file type.");
            }
        }
    }

}
