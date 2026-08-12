using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
        public void OnException(ExceptionContext context)
        {
            if (context.Exception is not ConcurrencyConflictException ex)
            {
                return;
            }

            context.Result = new ConflictObjectResult(ConcurrencyConflictResponse.Body(ex.Subject ?? "The record"));
            context.ExceptionHandled = true;
        }
    }
}
