using System;
using System.ComponentModel.DataAnnotations;

namespace Web.UpdateModels
{
    public abstract class VersionedUpdate
    {
        [RequiredETag]
        public string ETag { get; set; }
    }

    public sealed class RequiredETagAttribute : ValidationAttribute
    {
        public RequiredETagAttribute()
            : base("Refresh to load the current record before saving.") { }

        public override bool IsValid(object value) =>
            value is string tag && !string.IsNullOrWhiteSpace(tag) && !tag.Contains('*', StringComparison.Ordinal);
    }
}
