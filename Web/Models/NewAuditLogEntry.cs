using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.Models
{
    public class NewAuditLogEntry
    {
        public Guid Id { get; set; }

        public DateTime Time { get; set; }

        public string UserId { get; set; }

        public string UserDisplayName { get; set; }

        public string SubjectId { get; set; }

        public string SubjectName { get; set; }

        public string Action { get; set; }
    }
}
