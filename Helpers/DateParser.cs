using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.Helpers
{
    public static class DateParser
    {
        // Helper method to parse date or return null
        public static DateTime? ParseDateOrDefault(string dateString)
        {
            if (DateTime.TryParse(dateString, out DateTime parsedDate))
            {
                return parsedDate;
            }
            return null; // Return null if date is invalid
        }
    }
}
