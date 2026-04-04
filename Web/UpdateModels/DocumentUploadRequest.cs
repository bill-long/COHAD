using System;
using Microsoft.AspNetCore.Http;

namespace Web.UpdateModels
{
    public class DocumentUploadRequest
    {
        public string DisplayName { get; set; }

        public IFormFile File { get; set; }

        public Guid? FolderId { get; set; }
    }
}
