using System.Collections.Generic;
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
                _entries.Add(entry);
            }

            return Task.CompletedTask;
        }

        public Task<List<NewAuditLogEntry>> GetAllAsync()
        {
            lock (_entries)
            {
                return Task.FromResult(new List<NewAuditLogEntry>(_entries));
            }
        }
    }
}
