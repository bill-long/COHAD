using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockDocumentRepository : IDocumentRepository
    {
        private readonly Dictionary<Guid, ResidentDocument> _documents = new();

        public Task<List<ResidentDocument>> GetAllAsync()
        {
            lock (_documents)
            {
                return Task.FromResult(_documents.Values.Select(CloneDocument).ToList());
            }
        }

        public Task<ResidentDocument> GetByIdAsync(Guid id)
        {
            lock (_documents)
            {
                return Task.FromResult(_documents.TryGetValue(id, out var found) ? CloneDocument(found) : null);
            }
        }

        public Task<ResidentDocument> UpsertAsync(ResidentDocument document)
        {
            lock (_documents)
            {
                var copy = CloneDocument(document);
                if (copy.Id == Guid.Empty)
                {
                    copy.Id = Guid.NewGuid();
                }

                _documents[copy.Id] = copy;
                return Task.FromResult(CloneDocument(copy));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_documents)
            {
                _documents.Remove(id);
            }

            return Task.CompletedTask;
        }

        private static ResidentDocument CloneDocument(ResidentDocument document)
        {
            if (document == null)
            {
                return null;
            }

            return new ResidentDocument
            {
                Id = document.Id,
                DisplayName = document.DisplayName,
                OriginalFileName = document.OriginalFileName,
                BlobPath = document.BlobPath,
                ContentType = document.ContentType,
                SizeBytes = document.SizeBytes,
                UploadedByUniqueId = document.UploadedByUniqueId,
                CreatedUtc = document.CreatedUtc,
                FolderId = document.FolderId,
            };
        }
    }
}
