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
        /// Short noun for the conflicted record ("User", "Home"), used to build the client-facing
        /// 409 body. Null when the throw site predates the factory; consumers fall back to a
        /// generic subject.
        /// </summary>
        public string Subject { get; }

        public ConcurrencyConflictException(string message, Exception innerException)
            : base(message, innerException) { }

        private ConcurrencyConflictException(string subject, string message, Exception innerException)
            : base(message, innerException)
        {
            Subject = subject;
        }

        /// <summary>
        /// The one place the conflict wording is built, so log lines and alerts keyed on it cannot
        /// drift between repositories.
        /// </summary>
        public static ConcurrencyConflictException For(string subject, object id, Exception innerException) =>
            new(subject, $"{subject} {id} was modified by another request. Retry the operation.", innerException);
    }
}
