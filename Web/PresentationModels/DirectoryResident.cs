using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class DirectoryResident
    {
        public static DirectoryResident FromStorageModel(Resident storedResident)
        {
            var visibleEmails = new List<DirectoryEmailAddress>();

            if (storedResident.EmailAddresses != null)
            {
                visibleEmails.AddRange(storedResident.EmailAddresses.Where(e => e.VisibleInDirectory).Select(DirectoryEmailAddress.FromStorageModel));
            }

            var visiblePhones = new List<DirectoryPhoneNumber>();

            if (storedResident.PhoneNumbers != null)
            {
                visiblePhones.AddRange(storedResident.PhoneNumbers.Where(p => p.VisibleInDirectory).Select(DirectoryPhoneNumber.FromStorageModel));
            }

            return new DirectoryResident
            {
                GivenName = storedResident.GivenName,
                Surname = storedResident.Surname,
                EmailAddresses = visibleEmails,
                PhoneNumbers = visiblePhones
            };
        }

        public string GivenName { get; private set; }

        public string Surname { get; private set; }

        public List<DirectoryEmailAddress> EmailAddresses { get; private set; }

        public List<DirectoryPhoneNumber> PhoneNumbers { get; private set; }

        private DirectoryResident() { }
    }
}
