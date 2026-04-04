using System;

namespace Web.Models
{
    public class DocumentFolder
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
