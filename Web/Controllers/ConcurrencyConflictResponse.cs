namespace Web.Controllers
{
    /// <summary>
    /// Single definition of the 409 body returned when an optimistic-concurrency write loses its
    /// race, so the client-visible message and body shape cannot drift between call sites (the SPA
    /// services surface the <c>error</c> property).
    /// </summary>
    internal static class ConcurrencyConflictResponse
    {
        internal static object Body(string subject) =>
            new { error = $"{subject} was modified by another request. Please refresh and try again." };
    }
}
