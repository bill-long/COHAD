using System;

namespace Web.Models
{
    public class ResidentDocument
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; }

        public string OriginalFileName { get; set; }

        public string BlobPath { get; set; }

        public string ContentType { get; set; }

        public long SizeBytes { get; set; }

        public string UploadedByUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
