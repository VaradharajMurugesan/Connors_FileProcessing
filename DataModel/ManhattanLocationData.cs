using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    public class ManhattanLocationData
    {
        public string LocationExternalId { get; set; } // Location External Identifier
        public string LocationName { get; set; } // Location Name
        public string ManhattanWarehouseId { get; set; } // Manhattan Warehouse ID
        public string? FileFilter { get; set; } // File / Filter to be determined
    }
}
