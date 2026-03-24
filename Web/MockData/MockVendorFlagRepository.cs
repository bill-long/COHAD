using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockVendorFlagRepository : IVendorFlagRepository
    {
        private readonly Dictionary<Guid, VendorFlag> _flags = new();

        public Task<List<VendorFlag>> GetByVendorIdAsync(Guid vendorId)
        {
            lock (_flags)
            {
                var results = _flags.Values
                    .Where(f => f.VendorId == vendorId)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<VendorFlag> GetByIdAsync(Guid vendorId, Guid flagId)
        {
            lock (_flags)
            {
                if (_flags.TryGetValue(flagId, out var flag) && flag.VendorId == vendorId)
                {
                    return Task.FromResult(Clone(flag));
                }

                return Task.FromResult<VendorFlag>(null);
            }
        }

        public Task<VendorFlag> GetPendingByAuthorAsync(Guid vendorId, string authorUniqueId)
        {
            lock (_flags)
            {
                var flag = _flags.Values
                    .FirstOrDefault(f =>
                        f.VendorId == vendorId &&
                        string.Equals(f.AuthorUniqueId, authorUniqueId, StringComparison.OrdinalIgnoreCase) &&
                        f.Status == "Pending");
                return Task.FromResult(Clone(flag));
            }
        }

        public Task<List<VendorFlag>> GetAllPendingAsync()
        {
            lock (_flags)
            {
                return Task.FromResult(_flags.Values.Where(f => f.Status == "Pending").Select(Clone).ToList());
            }
        }

        public Task<VendorFlag> UpsertAsync(VendorFlag flag)
        {
            lock (_flags)
            {
                var clone = Clone(flag);
                if (clone.Id == Guid.Empty)
                {
                    clone.Id = Guid.NewGuid();
                }

                _flags[clone.Id] = clone;
                return Task.FromResult(Clone(clone));
            }
        }

        public Task DeleteAsync(Guid vendorId, Guid flagId)
        {
            lock (_flags)
            {
                if (_flags.TryGetValue(flagId, out var flag) && flag.VendorId == vendorId)
                {
                    _flags.Remove(flagId);
                }
            }

            return Task.CompletedTask;
        }

        private static VendorFlag Clone(VendorFlag flag)
        {
            if (flag == null)
            {
                return null;
            }

            return new VendorFlag
            {
                Id = flag.Id,
                VendorId = flag.VendorId,
                AuthorUniqueId = flag.AuthorUniqueId,
                AuthorDisplayName = flag.AuthorDisplayName,
                FlagNote = flag.FlagNote,
                Status = flag.Status,
                CreatedUtc = flag.CreatedUtc,
                ResolvedUtc = flag.ResolvedUtc
            };
        }
    }
}
