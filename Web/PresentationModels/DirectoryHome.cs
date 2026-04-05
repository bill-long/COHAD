using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class DirectoryHome
    {
        public static DirectoryHome FromStorageModel(Home storedHome, List<Resident> residents)
        {
            return new DirectoryHome
            {
                StreetName = storedHome.StreetName,
                StreetNumber = storedHome.StreetNumber,
                PhoneNumber = (storedHome.PhoneNumber?.VisibleInDirectory ?? false) ? DirectoryPhoneNumber.FromStorageModel(storedHome.PhoneNumber) : null,
                EmailAddress = (storedHome.EmailAddress?.VisibleInDirectory ?? false) ? DirectoryEmailAddress.FromStorageModel(storedHome.EmailAddress) : null,
                Residents = residents?.Select(DirectoryResident.FromStorageModel).ToList() ?? new List<DirectoryResident>()
            };
        }

        public int StreetNumber { get; private set; }

        public string StreetName { get; private set; }

        public DirectoryPhoneNumber PhoneNumber { get; private set; }

        public DirectoryEmailAddress EmailAddress { get; private set; }

        public List<DirectoryResident> Residents { get; private set; }

        private DirectoryHome() { }
    }
}
