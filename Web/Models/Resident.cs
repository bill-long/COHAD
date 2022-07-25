using System;
using System.Collections.Generic;

namespace Web.Models
{
    public class Resident
    {
        public string GivenName { get; set; }

        public string Surname { get; set; }

        public int YearOfBirth { get; set; }

        public List<EmailAddress> EmailAddresses { get; set; }

        public List<PhoneNumber> PhoneNumbers { get; set; }

        public Type ResidentType { get; set; }

        public enum Type
        {
            Homeowner,
            OtherAdult,
            Child
        }
    }
}
