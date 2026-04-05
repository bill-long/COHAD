using System;

namespace Web.Services.Repositories
{
    /// <summary>
    /// Thrown when an optimistic concurrency check fails (ETag mismatch).
    /// Callers should retry the read-modify-write cycle.
    /// </summary>
    public class ConcurrencyConflictException : Exception
    {
        public ConcurrencyConflictException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
