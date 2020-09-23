using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Web.Models;

namespace Web.Data
{
    public class Neighborhood
    {
        private static readonly Regex PhoneRegex = new Regex(@"\((\d\d\d)\) (\d\d\d)-(\d\d\d\d)");

        public static IEnumerable<Home> ReadInitialNeighborhoodData()
        {
            var homes = new List<Home>();

            var dataFromFile =
                JsonSerializer.Deserialize<dynamic>(File.ReadAllText(@"C:\Users\bill\Downloads\Neighborhood.json"));

            foreach (var row in dataFromFile.EnumerateArray())
            {
                var h = new Home
                {
                    StreetNumber = int.Parse(row.GetProperty("Street Number").GetString()),
                    StreetName = row.GetProperty("Street Name").GetString(),
                    PhoneNumber = GetPhoneNumberFromString(row.GetProperty("HomePhone").GetString(), "Home"),
                    Residents = new List<Resident>(),
                    Id = Guid.NewGuid()
                };

                var resident1 = GetResident(row.GetProperty("Resident1Name").GetString(),
                    row.GetProperty("Resident1Email").GetString(), row.GetProperty("Resident1Phone").GetString());

                if (resident1 != null)
                {
                    h.Residents.Add(resident1);
                }

                var resident2 = GetResident(row.GetProperty("Resident2Name").GetString(),
                    row.GetProperty("Resident2Email").GetString(), row.GetProperty("Resident2Phone").GetString());

                if (resident2 != null)
                {
                    h.Residents.Add(resident2);
                }

                homes.Add(h);
            }

            return homes;
        }

        private static Resident GetResident(string name, string email, string phone)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var lastSpace = name.LastIndexOf(' ');

            var given = name.Substring(0, lastSpace + 1);

            var sur = name.Substring(lastSpace + 1);

            var emails = new List<EmailAddress>();

            if (!string.IsNullOrEmpty(email))
            {
                emails.Add(new EmailAddress {Address = email, GroupEmailOptedIn = true, VisibleInDirectory = true});
            }

            var phoneNumbers = new List<PhoneNumber>();

            if (!string.IsNullOrEmpty(phone))
            {
                phoneNumbers.Add(GetPhoneNumberFromString(phone, "Mobile"));
            }

            return new Resident
            {
                EmailAddresses = emails, GivenName = given, PhoneNumbers = phoneNumbers, Surname = sur
            };
        }

        private static PhoneNumber GetPhoneNumberFromString(string phoneNumber, string type)
        {
            if (string.IsNullOrEmpty(phoneNumber))
            {
                return null;
            }

            var matches = PhoneRegex.Matches(phoneNumber);

            return new PhoneNumber
            {
                AreaCode = int.Parse(matches[0].Groups[1].Value),
                Prefix = int.Parse(matches[0].Groups[2].Value),
                LineNumber = int.Parse(matches[0].Groups[3].Value),
                VisibleInDirectory = true,
                Type = type
            };
        }
    }
}
