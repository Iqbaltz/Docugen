using Docugen.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Docugen.Api;

/// <summary>Turns known report failures into ProblemDetails responses with a useful status code.</summary>
internal sealed class DocugenExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            ReportLimitExceededException limit => (
                StatusCodes.Status422UnprocessableEntity,
                "Too many rows",
                $"{limit.Message} Narrow the filters or choose another format (xlsx or csv)."),
            ReportQueryTimeoutException => (
                StatusCodes.Status504GatewayTimeout,
                "Query timed out",
                "The report query took too long. Narrow the filters or try again later."),
            ReportDefinitionMismatchException mismatch => (
                StatusCodes.Status500InternalServerError,
                "Report definition does not match the query",
                mismatch.Message),
            _ => (0, null, null),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}
