using System;

namespace Web.Services
{
    public static class ImageContentTypes
    {
        /// <summary>Trusted image/* MIME for blob metadata from an already-validated file extension.</summary>
        public static string FromExtension(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => throw new ArgumentOutOfRangeException(nameof(extension), extension, "Extension must be allowed by the caller's allowlist.")
            };
        }
    }
}
