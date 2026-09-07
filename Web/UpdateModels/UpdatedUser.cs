using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.UpdateModels
{
    public class UpdatedUser : VersionedUpdate
    {
        public string GivenName { get; set; }

        public string Surname { get; set; }

        public string StreetAddress { get; set; }

        public string UniqueId { get; set; }
    }
}
