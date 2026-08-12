using System;

namespace Web.Services.Repositories
{
    /// <summary>
    /// Thrown when an optimistic concurrency check fails (ETag mismatch, or the document a
    /// caller-supplied ETag refers to no longer exists). Callers should retry the
    /// read-modify-write cycle. Unhandled instances are mapped to a 409 response by
    /// <c>ConcurrencyConflictExceptionFilter</c>.
    /// </summary>
    public class ConcurrencyConflictException : Exception
    {
        /// <summary>
        /// Generic noun for throw sites that do not name a record type, so <see cref="Subject"/>
        /// is never null and consumers never have to supply their own fallback wording.
        /// </summary>
        internal const string GenericSubject = "The record";

        /// <summary>
        /// Short noun for the conflicted record ("User", "Home"), used to build the client-facing
        /// 409 body. Throw sites that predate the factory report <see cref="GenericSubject"/>.
        /// </summary>
        public string Subject { get; }

        public ConcurrencyConflictException(string message, Exception innerException)
            : base(message, innerException)
        {
            Subject = GenericSubject;
        }

        private ConcurrencyConflictException(string subject, string message, Exception innerException)
            : base(message, innerException)
        {
            Subject = subject;
        }

        /// <summary>
        /// The one place the User/Home repositories (Cosmos and Mock) build their conflict
        /// wording, so it cannot drift between them. Older subsystems (email suppression) still
        /// word their own conflicts through the public constructor.
        /// </summary>
        public static ConcurrencyConflictException For(string subject, object id, Exception innerException) =>
            new(subject, $"{subject} {id} was modified by another request. Retry the operation.", innerException);
    }
}
