using System;
using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class UpdatedUserAssociations
    {
        public List<string> RoleNames { get; set; } = new();

        public List<Guid> OwnedHomeIds { get; set; } = new();
    }
}
