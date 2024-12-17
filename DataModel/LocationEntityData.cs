using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class LocationEntityData
    {
        public string LocationId { get; set; } // Location Unique Identifier
        public string LocationExternalId { get; set; } // Location External Identifier
        public string LocationName { get; set; } // Location Name
        public DateTime? LastModifiedDate { get; set; } // Last Modified Date
        public string LastModifiedByEmployeeId { get; set; } // Last Modified By Employee ID
        public string LastModifiedByEmployeeExternalId { get; set; } // Last Modified By Employee External ID
        public string Status { get; set; } // Status of the Location
        public string Contact { get; set; } // Contact Information
        public string Address { get; set; } // Address of the Location
        public string TimeZone { get; set; } // TimeZone for the Location
        public string StartOfWeek { get; set; } // Start of Week (e.g., Sunday)
        public string LocationType { get; set; } // Type of the Location (e.g., Real, District)
        public string LocationSubType { get; set; } // Subtype of the Location
        public string ParentLocationId { get; set; } // Parent Location ID
        public DateTime? EffectiveDate { get; set; } // Effective Date for the Location
    }
}
