using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TallyNext.Application.Common;
using TallyNext.Domain.Common;
using TallyNext.Infrastructure.Server.Sync;

namespace TallyNext.Api.Infrastructure;

/// <summary>
/// Maps expected failures to RFC 7807 problem details with a stable <c>code</c>. Unexpected exceptions produce a
/// generic 500 without any internal detail (the trace id links the response to the server log).
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code, extensions) = exception switch
        {
            ValidationFailedException v => (StatusCodes.Status400BadRequest, v.Message, v.Code, (object?)v.Errors),
            DomainException d => (StatusCodes.Status400BadRequest, d.Message, d.Code, null),
            NotFoundException n => (StatusCodes.Status404NotFound, n.Message, n.Code, null),
            ForbiddenException f => (StatusCodes.Status403Forbidden, f.Message, f.Code, null),
            AppException { Code: "unauthenticated" or "auth.invalid_credentials" or "auth.session_expired" or "auth.access_revoked" } a => (StatusCodes.Status401Unauthorized, a.Message, a.Code, null),
            ConflictException c => (StatusCodes.Status409Conflict, c.Message, c.Code, c.CurrentVersion),
            SyncVersionUnsupportedException s => (StatusCodes.Status426UpgradeRequired, s.Message, "sync.version_unsupported", null),
            BadHttpRequestException b => (StatusCodes.Status400BadRequest, "The request body or parameters could not be read.", "request.invalid", null),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Request cancelled.", "request.cancelled", null),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "server.error", null),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        switch (extensions)
        {
            case IReadOnlyDictionary<string, string[]> errors when errors.Count > 0:
                problem.Extensions["errors"] = errors;
                break;
            case long currentVersion:
                problem.Extensions["currentVersion"] = currentVersion;
                break;
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }
}
