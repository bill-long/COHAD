using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockYouthServiceListingRepository : IYouthServiceListingRepository
    {
        private readonly Dictionary<Guid, YouthServiceListing> _listings = new();

        public Task<List<YouthServiceListing>> GetAllAsync()
        {
            lock (_listings)
            {
                return Task.FromResult(_listings.Values.Select(Clone).ToList());
            }
        }

        public Task<YouthServiceListing> GetByIdAsync(Guid id)
        {
            lock (_listings)
            {
                return Task.FromResult(_listings.TryGetValue(id, out var listing) ? Clone(listing) : null);
            }
        }

        public Task<YouthServiceListing> UpsertAsync(YouthServiceListing listing)
        {
            lock (_listings)
            {
                var clone = Clone(listing);
                if (clone.Id == Guid.Empty)
                {
                    clone.Id = Guid.NewGuid();
                }

                _listings[clone.Id] = clone;
                return Task.FromResult(Clone(clone));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_listings)
            {
                _listings.Remove(id);
            }

            return Task.CompletedTask;
        }

        private static YouthServiceListing Clone(YouthServiceListing listing)
        {
            if (listing == null)
            {
                return null;
            }

            return new YouthServiceListing
            {
                Id = listing.Id,
                Name = listing.Name,
                Services = listing.Services?.ToList() ?? new List<string>(),
                BornYear = listing.BornYear,
                Phone = listing.Phone,
                ContactMethod = listing.ContactMethod,
                Email = listing.Email,
                Address = listing.Address,
                ParentNote = listing.ParentNote,
                CreatedByUniqueId = listing.CreatedByUniqueId,
                ModifiedByUniqueId = listing.ModifiedByUniqueId,
                CreatedUtc = listing.CreatedUtc,
                ModifiedUtc = listing.ModifiedUtc
            };
        }
    }
}
