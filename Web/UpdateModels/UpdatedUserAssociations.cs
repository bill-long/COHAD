using System;
using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class UpdatedUserAssociations
    {
        public List<string> RoleNames { get; set; } = new();

        public List<Guid> OwnedHomeIds { get; set; } = new();

        /// <summary>
        /// Optional resident this account is linked to. Null (or an omitted property) leaves the
        /// stored link unchanged, so clients that predate the link cannot wipe it; <see cref="Guid.Empty"/>
        /// clears it explicitly; any other value sets it. A set resident must be an adult in one of
        /// <see cref="OwnedHomeIds"/>.
        /// </summary>
        public Guid? ResidentId { get; set; }
    }
}
