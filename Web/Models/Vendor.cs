using System;
using System.Collections.Generic;

namespace Web.Models
{
    public class Vendor
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public List<string> Categories { get; set; } = new List<string>();

        public bool IsNeighborAffiliated { get; set; }

        public string Phone { get; set; }

        public string Email { get; set; }

        public string Website { get; set; }

        public string Address { get; set; }

        public string Notes { get; set; }

        public string CreatedByUniqueId { get; set; }

        public string ModifiedByUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }
    }
}
