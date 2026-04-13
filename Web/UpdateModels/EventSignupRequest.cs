using System;
using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class EventSignupRequest
    {
        /// <summary>
        /// The home to sign up for. Null/empty for users not yet associated with a home.
        /// </summary>
        public Guid? HomeId { get; set; }

        public int Adults { get; set; }

        public int Children { get; set; }

        public List<string> AdultNames { get; set; } = new List<string>();

        public List<string> ChildNames { get; set; } = new List<string>();
    }
}
