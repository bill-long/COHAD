using System;
using Microsoft.AspNetCore.Http;

namespace Web.UpdateModels
{
    public class EventUpsertRequest
    {
        public Guid? Id { get; set; }

        public string Title { get; set; }

        public string Description { get; set; }

        public DateTime? StartUtc { get; set; }

        public bool AllowSignups { get; set; }

        public bool RemovePromoMedia { get; set; }

        public IFormFile PromotionalAsset { get; set; }
    }
}
