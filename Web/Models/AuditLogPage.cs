#nullable enable
using System.Collections.Generic;

namespace Web.Models
{
    public class AuditLogPage
    {
        public List<NewAuditLogEntry> Items { get; set; } = new List<NewAuditLogEntry>();

        public string? ContinuationToken { get; set; }

        public bool HasMore { get; set; }
    }
}
