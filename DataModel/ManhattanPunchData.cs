using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.DataModel
{
    public class ManhattanPunchData
    {
        public string TimeClockChangeId { get; set; }
        public string LocationId { get; set; }
        public string LocationExternalId { get; set; }
        public DateTime? LastModifiedDate { get; set; }
        public string LastModifiedByEmployeeId { get; set; }
        public string LastModifiedByExternalEmployeeId { get; set; }
        public string EmployeeId { get; set; }
        public string EmployeeExternalId { get; set; }
        public string TimeClockId { get; set; }
        public string ClockType { get; set; }
        public DateTime? ClockTimeBeforeChange { get; set; }
        public string ClockLocationIdBeforeChange { get; set; }
        public string ClockLocationBeforeChange { get; set; }
        public string ClockWorkRoleIdBeforeChange { get; set; }
        public string ClockWorkRoleBeforeChange { get; set; }
        public string ClockSourceBeforeChange { get; set; }
        public string ClockNoteBeforeChange { get; set; }
        public DateTime? ClockTimeAfterChange { get; set; }
        public string ClockLocationIdAfterChange { get; set; }
        public string ClockLocationAfterChange { get; set; }
        public string ClockWorkRoleIdAfterChange { get; set; }
        public string ClockWorkRoleAfterChange { get; set; }
        public string ClockSourceAfterChange { get; set; }
        public string ClockNoteAfterChange { get; set; }
        public string EventType { get; set; }
    }
}
