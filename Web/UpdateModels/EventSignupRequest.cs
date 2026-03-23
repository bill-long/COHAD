using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class EventSignupRequest
    {
        public int Adults { get; set; }

        public int Children { get; set; }

        public List<string> AdultNames { get; set; } = new List<string>();

        public List<string> ChildNames { get; set; } = new List<string>();
    }
}
