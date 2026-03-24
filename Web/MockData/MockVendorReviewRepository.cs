using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockVendorReviewRepository : IVendorReviewRepository
    {
        private readonly Dictionary<Guid, VendorReview> _reviews = new();

        public Task<List<VendorReview>> GetByVendorIdAsync(Guid vendorId)
        {
            lock (_reviews)
            {
                var results = _reviews.Values
                    .Where(r => r.VendorId == vendorId)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<VendorReview> GetByIdAsync(Guid vendorId, Guid reviewId)
        {
            lock (_reviews)
            {
                if (_reviews.TryGetValue(reviewId, out var review) && review.VendorId == vendorId)
                {
                    return Task.FromResult(Clone(review));
                }

                return Task.FromResult<VendorReview>(null);
            }
        }

        public Task<VendorReview> UpsertAsync(VendorReview review)
        {
            lock (_reviews)
            {
                var clone = Clone(review);
                if (clone.Id == Guid.Empty)
                {
                    clone.Id = Guid.NewGuid();
                }

                _reviews[clone.Id] = clone;
                return Task.FromResult(Clone(clone));
            }
        }

        public Task DeleteAsync(Guid vendorId, Guid reviewId)
        {
            lock (_reviews)
            {
                if (_reviews.TryGetValue(reviewId, out var review) && review.VendorId == vendorId)
                {
                    _reviews.Remove(reviewId);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteByVendorCascadeAsync(Guid vendorId, Guid reviewId) => DeleteAsync(vendorId, reviewId);

        public Task<IReadOnlyDictionary<Guid, int>> GetReviewCountsByVendorAsync()
        {
            lock (_reviews)
            {
                var counts = _reviews.Values
                    .GroupBy(r => r.VendorId)
                    .ToDictionary(g => g.Key, g => g.Count());
                return Task.FromResult<IReadOnlyDictionary<Guid, int>>(counts);
            }
        }

        public Task<IReadOnlyDictionary<Guid, DateTime>> GetLatestReviewModifiedUtcByVendorAsync()
        {
            lock (_reviews)
            {
                var latest = _reviews.Values
                    .GroupBy(r => r.VendorId)
                    .ToDictionary(g => g.Key, g => g.Max(r => r.ModifiedUtc));
                return Task.FromResult<IReadOnlyDictionary<Guid, DateTime>>(latest);
            }
        }

        private static VendorReview Clone(VendorReview review)
        {
            if (review == null)
            {
                return null;
            }

            return new VendorReview
            {
                Id = review.Id,
                VendorId = review.VendorId,
                AuthorUniqueId = review.AuthorUniqueId,
                AuthorDisplayName = review.AuthorDisplayName,
                ReviewText = review.ReviewText,
                CreatedUtc = review.CreatedUtc,
                ModifiedUtc = review.ModifiedUtc
            };
        }
    }
}
