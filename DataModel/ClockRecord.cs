using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class ClockRecord
    {
        public string LocationExternalId { get; set; } // External Location ID        
        public int EmployeeExternalId { get; set; } // External Employee ID
        public string ClockType { get; set; } // Type of clock event (e.g., ShiftBegin, MealBreakBegin)
        public DateTime? ClockTimeBeforeChange { get; set; } // Clock time before the change (nullable)        
        public DateTime? ClockTimeAfterChange { get; set; } // Clock time after the change
        public string ClockWorkRoleAfterChange { get; set; } // Work Role description after the change
        public string EventType { get; set; } // Type of event (e.g., Create, ApproveReject)
        public string LocationName { get; set; } // Location Name
        public string ManhattanWarehouseId { get; set; } // Manhattan Warehouse ID
    }

    // Model class representing a grouped shift
    public class ShiftGroup
    {
        public int EmployeeExternalId { get; set; }
        public List<ClockRecord> Records { get; set; } = new List<ClockRecord>();
    }
}
