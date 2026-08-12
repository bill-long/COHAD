using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Controllers;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class ConcurrencyConflictExceptionFilterTests
{
    private static ExceptionContext MakeContext(Exception exception)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };
    }

    [Fact]
    public void Maps_conflict_to_409_with_the_shared_body()
    {
        var filter = new ConcurrencyConflictExceptionFilter(NullLogger<ConcurrencyConflictExceptionFilter>.Instance);
        var context = MakeContext(
            ConcurrencyConflictException.For("User", "u-1", new InvalidOperationException("ETag mismatch"))
        );

        filter.OnException(context);

        Assert.True(context.ExceptionHandled);
        var conflict = Assert.IsType<ConflictObjectResult>(context.Result);
        var error = conflict.Value!.GetType().GetProperty("error")!.GetValue(conflict.Value) as string;
        Assert.Equal("User was modified by another request. Please refresh and try again.", error);
    }

    [Fact]
    public void Falls_back_to_a_generic_subject_when_the_exception_carries_none()
    {
        // Older throw sites use the plain constructor and have no Subject; the 409 must still be
        // truthful rather than becoming a 500.
        var filter = new ConcurrencyConflictExceptionFilter(NullLogger<ConcurrencyConflictExceptionFilter>.Instance);
        var context = MakeContext(
            new ConcurrencyConflictException("legacy message", new InvalidOperationException("ETag mismatch"))
        );

        filter.OnException(context);

        Assert.True(context.ExceptionHandled);
        var conflict = Assert.IsType<ConflictObjectResult>(context.Result);
        var error = conflict.Value!.GetType().GetProperty("error")!.GetValue(conflict.Value) as string;
        Assert.Equal("The record was modified by another request. Please refresh and try again.", error);
    }

    [Fact]
    public void Ignores_other_exceptions()
    {
        var filter = new ConcurrencyConflictExceptionFilter(NullLogger<ConcurrencyConflictExceptionFilter>.Instance);
        var context = MakeContext(new InvalidOperationException("unrelated"));

        filter.OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }
}
