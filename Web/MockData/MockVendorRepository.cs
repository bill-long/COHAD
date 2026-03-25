using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockVendorRepository : IVendorRepository
    {
        private readonly Dictionary<Guid, Vendor> _vendors = new();

        public Task<List<Vendor>> GetAllAsync()
        {
            lock (_vendors)
            {
                return Task.FromResult(_vendors.Values.Select(Clone).ToList());
            }
        }

        public Task<Vendor> GetByIdAsync(Guid id)
        {
            lock (_vendors)
            {
                return Task.FromResult(_vendors.TryGetValue(id, out var vendor) ? Clone(vendor) : null);
            }
        }

        public Task<Vendor> UpsertAsync(Vendor vendor)
        {
            lock (_vendors)
            {
                var clone = Clone(vendor);
                if (clone.Id == Guid.Empty)
                {
                    clone.Id = Guid.NewGuid();
                }

                _vendors[clone.Id] = clone;
                return Task.FromResult(Clone(clone));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_vendors)
            {
                _vendors.Remove(id);
            }

            return Task.CompletedTask;
        }

        private static Vendor Clone(Vendor vendor)
        {
            if (vendor == null)
            {
                return null;
            }

            return new Vendor
            {
                Id = vendor.Id,
                Name = vendor.Name,
                Categories = vendor.Categories?.ToList() ?? new List<string>(),
                IsNeighborAffiliated = vendor.IsNeighborAffiliated,
                Phone = vendor.Phone,
                Email = vendor.Email,
                Website = vendor.Website,
                Address = vendor.Address,
                Notes = vendor.Notes,
                CreatedByUniqueId = vendor.CreatedByUniqueId,
                ModifiedByUniqueId = vendor.ModifiedByUniqueId,
                CreatedUtc = vendor.CreatedUtc,
                ModifiedUtc = vendor.ModifiedUtc
            };
        }
    }
}
