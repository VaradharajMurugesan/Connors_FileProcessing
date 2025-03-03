using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class ClockRecord
    {
        public int Id { get; set; }
        public string TimeClockChangeId { get; set; }
        public string LocationId { get; set; }
        public int LocationExternalId { get; set; }
        public DateTime LastModifiedDt { get; set; }
        public string LastModifiedByEmployeeId { get; set; }
        public int LastModifiedByExternalEmployeeId { get; set; }
        public string EmployeeId { get; set; }
        public int EmployeeExternalId { get; set; }
        public string TimeClockId { get; set; }
        public string ClockType { get; set; }
        public DateTime? ClockTimeBeforeChange { get; set; }
        public string ClockLocationIdBeforeChange { get; set; }
        public int ClockLocationBeforeChange { get; set; }
        public string ClockWorkRoleIdBeforeChange { get; set; }
        public string ClockWorkRoleBeforeChange { get; set; }
        public string ClockSourceBeforeChange { get; set; }
        public string ClockNoteBeforeChange { get; set; }
        public DateTime? ClockTimeAfterChange { get; set; }
        public string ClockLocationIdAfterChange { get; set; }
        public int ClockLocationAfterChange { get; set; }
        public string ClockWorkRoleIdAfterChange { get; set; }
        public string ClockWorkRoleAfterChange { get; set; }
        public string ClockSourceAfterChange { get; set; }
        public string ClockNoteAfterChange { get; set; }
        public string EventType { get; set; }
        public bool IsCurrent { get; set; }
        public string InsertBatchId { get; set; }
        public DateTime InsertLoadDt { get; set; }
        public string ManhattanWarehouseId { get; set; } // Manhattan Warehouse ID
    }

    // Model class representing a grouped shift
    public class ShiftGroup
    {
        public int EmployeeExternalId { get; set; }
        public List<ClockRecord> Records { get; set; } = new List<ClockRecord>();
    }
}
