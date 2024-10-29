using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.DataModel
{
    // HR Data Model
    public class EmployeeHrData
    {
        public string EmployeeId { get; set; }
        public string EmployeeExternalId { get; set; }
        public long LegionUserId { get; set; }
        public string LocationId { get; set; }
        public string LocationExternalId { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string MiddleInitial { get; set; }
        public string NickName { get; set; }
        public string Title { get; set; }
        public string Email { get; set; }
        public string PhoneNumber { get; set; }
        public string Status { get; set; }
        public string ManagerId { get; set; }
        public bool Salaried { get; set; }
        public bool Hourly { get; set; }
        public bool Exempt { get; set; }
        public decimal HourlyRate { get; set; }
        public string LegionUserFirstName { get; set; }
        public string LegionUserLastName { get; set; }
        public string LegionUserNickName { get; set; }
        public string LegionUserEmail { get; set; }
        public string LegionUserPhoneNumber { get; set; }
        public string LegionUserAddress { get; set; }
        public string LegionUserPhoto { get; set; }
        public string LegionUserBusinessPhoto { get; set; }
        public string CompanyId { get; set; }
        public string CompanyName { get; set; }
    }
}
