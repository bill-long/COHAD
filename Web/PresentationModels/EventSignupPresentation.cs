using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class EventSignupPresentation
    {
        public string HomeId { get; private set; }

        public string HomeAddress { get; private set; }

        public string UserDisplayName { get; private set; }

        public string UserEmail { get; private set; }

        public int Adults { get; private set; }

        public int Children { get; private set; }

        public List<string> AdultNames { get; private set; }

        public List<string> ChildNames { get; private set; }

        public System.DateTime SignedUpUtc { get; private set; }

        public static EventSignupPresentation FromStorageModel(EventSignup signup)
        {
            if (signup == null)
            {
                return null;
            }

            return new EventSignupPresentation
            {
                HomeId = signup.HomeId != Guid.Empty ? signup.HomeId.ToString("D") : null,
                HomeAddress = signup.HomeAddress,
                UserDisplayName = signup.UserDisplayName,
                UserEmail = signup.UserEmail,
                Adults = signup.Adults,
                Children = signup.Children,
                AdultNames =
                    signup.AdultNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                ChildNames =
                    signup.ChildNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                SignedUpUtc = signup.SignedUpUtc,
            };
        }
    }
}
