using System;

namespace Web.Models
{
    public class AuditLogEntry
    {
        public DateTime Time { get; set; }

        public string UserId { get; set; }

        public string UserDisplayName { get; set; }

        public string Action { get; set; }
    }
}
