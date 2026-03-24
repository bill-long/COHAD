using System;
using System.Collections.Generic;

namespace Web.Models
{
    public enum PreferredContactMethod
    {
        Call = 0,
        Text = 1
    }

    public class YouthServiceListing
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public List<string> Services { get; set; } = new List<string>();

        public int? BornYear { get; set; }

        public string Phone { get; set; }

        public PreferredContactMethod ContactMethod { get; set; } = PreferredContactMethod.Text;

        public string Email { get; set; }

        public string Address { get; set; }

        public string ParentNote { get; set; }

        public string CreatedByUniqueId { get; set; }

        public string ModifiedByUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }
    }
}
