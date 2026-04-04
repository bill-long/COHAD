using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class DocumentFolderSummary
    {
        public Guid Id { get; private set; }

        public string Name { get; private set; }

        public int SortOrder { get; private set; }

        public int DocumentCount { get; private set; }

        public static DocumentFolderSummary FromStorageModel(DocumentFolder folder, int documentCount)
        {
            return new DocumentFolderSummary
            {
                Id = folder.Id,
                Name = folder.Name,
                SortOrder = folder.SortOrder,
                DocumentCount = documentCount
            };
        }
    }
}
