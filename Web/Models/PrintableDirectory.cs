using System;

namespace Web.Models
{
    public class PrintableDirectory
    {
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public string CreatedBy { get; set; }

        public DateTime LastUpdated { get; set; }

        public string LastUpdatedBy { get; set; }

        public string FrontCoverDataUrl { get; set; }

        public string BackCoverDataUrl { get; set; }

        public bool AddExtraPageBreak { get; set; }
    }
}
