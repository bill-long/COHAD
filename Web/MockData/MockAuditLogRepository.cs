using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockAuditLogRepository : IAuditLogRepository
    {
        private readonly List<NewAuditLogEntry> _entries = new();

        public Task AddAsync(NewAuditLogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(CloneEntry(entry));
            }

            return Task.CompletedTask;
        }

        public Task<List<NewAuditLogEntry>> GetAllAsync()
        {
            lock (_entries)
            {
                return Task.FromResult(_entries.Select(CloneEntry).ToList());
            }
        }

        private static NewAuditLogEntry CloneEntry(NewAuditLogEntry e)
        {
            if (e == null)
            {
                return null;
            }

            return new NewAuditLogEntry
            {
                Id = e.Id,
                Time = e.Time,
                UserId = e.UserId,
                UserDisplayName = e.UserDisplayName,
                SubjectId = e.SubjectId,
                SubjectName = e.SubjectName,
                Action = e.Action
            };
        }
    }
}
