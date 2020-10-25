using System;
using System.Collections.Generic;

namespace Web.Models
{
    public class Home
    {
        public Guid Id { get; set; }

        public int StreetNumber { get; set; }

        public string StreetName { get; set; }

        public PhoneNumber PhoneNumber { get; set; }

        public EmailAddress EmailAddress { get; set; }

        public List<Resident> Residents { get; set; }
    }
}
