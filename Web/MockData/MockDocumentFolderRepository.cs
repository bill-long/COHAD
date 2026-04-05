using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockDocumentFolderRepository : IDocumentFolderRepository
    {
        private readonly Dictionary<Guid, DocumentFolder> _folders = new();

        public MockDocumentFolderRepository()
        {
            SeedData();
        }

        public Task<List<DocumentFolder>> GetAllAsync()
        {
            lock (_folders)
            {
                return Task.FromResult(_folders.Values.Select(CloneFolder).ToList());
            }
        }

        public Task<DocumentFolder> GetByIdAsync(Guid id)
        {
            lock (_folders)
            {
                return Task.FromResult(_folders.TryGetValue(id, out var found) ? CloneFolder(found) : null);
            }
        }

        public Task<DocumentFolder> UpsertAsync(DocumentFolder folder)
        {
            lock (_folders)
            {
                var copy = CloneFolder(folder);
                if (copy.Id == Guid.Empty)
                {
                    copy.Id = Guid.NewGuid();
                }

                _folders[copy.Id] = copy;
                return Task.FromResult(CloneFolder(copy));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_folders)
            {
                _folders.Remove(id);
            }

            return Task.CompletedTask;
        }

        internal static readonly Guid GoverningDocsFolderId = new("a0000001-0000-0000-0000-000000000001");
        internal static readonly Guid MeetingMinutesFolderId = new("a0000001-0000-0000-0000-000000000002");
        internal static readonly Guid FinancialReportsFolderId = new("a0000001-0000-0000-0000-000000000003");

        private void SeedData()
        {
            var folders = new[]
            {
                new DocumentFolder
                {
                    Id = GoverningDocsFolderId,
                    Name = "CC&Rs & Governing Docs",
                    SortOrder = 0,
                    CreatedUtc = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc),
                },
                new DocumentFolder
                {
                    Id = MeetingMinutesFolderId,
                    Name = "Meeting Minutes",
                    SortOrder = 1,
                    CreatedUtc = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                },
                new DocumentFolder
                {
                    Id = FinancialReportsFolderId,
                    Name = "Financial Reports",
                    SortOrder = 2,
                    CreatedUtc = new DateTime(2025, 3, 10, 0, 0, 0, DateTimeKind.Utc),
                },
            };

            foreach (var folder in folders)
            {
                _folders[folder.Id] = folder;
            }
        }

        private static DocumentFolder CloneFolder(DocumentFolder folder)
        {
            if (folder == null)
            {
                return null;
            }

            return new DocumentFolder
            {
                Id = folder.Id,
                Name = folder.Name,
                SortOrder = folder.SortOrder,
                CreatedUtc = folder.CreatedUtc,
            };
        }
    }
}
