using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class ResidentDocumentSummary
    {
        public Guid Id { get; private set; }

        public string DisplayName { get; private set; }

        public string ContentType { get; private set; }

        public long SizeBytes { get; private set; }

        public DateTime CreatedUtc { get; private set; }

        public static ResidentDocumentSummary FromStorageModel(ResidentDocument document)
        {
            return new ResidentDocumentSummary
            {
                Id = document.Id,
                DisplayName = document.DisplayName,
                ContentType = document.ContentType,
                SizeBytes = document.SizeBytes,
                CreatedUtc = document.CreatedUtc
            };
        }
    }
}
