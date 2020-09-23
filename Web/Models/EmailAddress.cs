using System;

namespace Web.Models
{
    public class EmailAddress
    {
        public string Address { get; set; }

        public bool VisibleInDirectory { get; set; }

        public bool GroupEmailOptedIn { get; set; }
    }
}
