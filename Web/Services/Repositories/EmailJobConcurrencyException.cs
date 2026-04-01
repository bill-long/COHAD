using System;

namespace Web.Services.Repositories
{
    /// <summary>
    /// Thrown when an email job replace fails because the document was modified
    /// elsewhere (Cosmos precondition failed / ETag mismatch).
    /// </summary>
    public sealed class EmailJobConcurrencyException : Exception
    {
        public EmailJobConcurrencyException()
            : base("The email job was modified by another operation.")
        {
        }
    }
}
