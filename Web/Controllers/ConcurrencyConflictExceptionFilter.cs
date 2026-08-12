using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Web.Services.Repositories;

namespace Web.Controllers
{
    /// <summary>
    /// Maps any unhandled <see cref="ConcurrencyConflictException"/> to a 409 with the shared
    /// <see cref="ConcurrencyConflictResponse"/> body. Registered globally, so an endpoint that
    /// performs an ETag-checked write gets the correct conflict response without remembering a
    /// per-site try/catch - a lost race must never surface as a 500. Sites that need per-record
    /// handling (skip-and-continue loops, background sweeps) still catch the exception themselves,
    /// which wins before this filter runs.
    /// </summary>
    public sealed class ConcurrencyConflictExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<ConcurrencyConflictExceptionFilter> _logger;

        public ConcurrencyConflictExceptionFilter(ILogger<ConcurrencyConflictExceptionFilter> logger)
        {
            _logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            if (context.Exception is not ConcurrencyConflictException ex)
            {
                return;
            }

            // Handling the exception here takes it away from the global error handler, so log it:
            // an endpoint that conflicts every time (a caller reusing a stale model instance, a
            // Mock/Cosmos divergence) would otherwise show up only as bare 409s in telemetry, with
            // nothing naming the record. Warning, because production captures Warning and above.
            _logger.LogWarning(
                ex,
                "Concurrency conflict on {Subject} for {Path}; returning 409.",
                ex.Subject,
                context.HttpContext?.Request?.Path.ToString()
            );

            context.Result = new ConflictObjectResult(ConcurrencyConflictResponse.Body(ex.Subject));
            context.ExceptionHandled = true;
        }
    }
}
