using System;
using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class VendorUpsertRequest
    {
        public Guid? Id { get; set; }

        public string Name { get; set; }

        public List<string> Categories { get; set; } = new List<string>();

        public bool IsNeighborAffiliated { get; set; }

        public string Phone { get; set; }

        public string Email { get; set; }

        public string Website { get; set; }

        public string Address { get; set; }

        public string Notes { get; set; }

        public string InitialReviewText { get; set; }
    }
}
