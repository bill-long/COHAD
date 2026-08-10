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

        /// <summary>
        /// When true, removes the caller's existing signup instead of creating or updating one.
        /// Counts are ignored. Removal is idempotent: removing an already-removed signup succeeds.
        /// </summary>
        public bool Remove { get; set; }

        public List<string> AdultNames { get; set; } = new List<string>();

        public List<string> ChildNames { get; set; } = new List<string>();
    }
}
