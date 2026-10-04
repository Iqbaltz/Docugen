using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Docugen.Core;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Docugen.Api.Endpoints;

public static partial class ReportEndpoints
{
    public const string RenderRateLimiter = "render";

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization().WithTags("Reports");

        group.MapGet("/", ListReports)
            .WithName("ListReports")
            .WithSummary("Lists the reports the caller is allowed to run.");

        group.MapGet("/{key}", GetReport)
            .WithName("GetReport")
            .WithSummary("Describes a report's parameters, formats and columns.");

        group.MapPost("/{key}/render", RenderReport)
            .WithName("RenderReport")
            .WithSummary("Runs a report and returns the document as a file download.")
            .RequireRateLimiting(RenderRateLimiter)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    private static Ok<ReportSummary[]> ListReports(IReportCatalog catalog, ClaimsPrincipal user) =>
        TypedResults.Ok(catalog.All
            .Where(r => ReportAccess.CanRun(user, r))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ReportSummary.From)
            .ToArray());

    private static Results<Ok<ReportDetails>, ProblemHttpResult, ForbidHttpResult> GetReport(string key, IReportCatalog catalog, ClaimsPrincipal user)
    {
        var report = catalog.Find(key);
        if (report is null)
        {
            return ReportNotFound(key);
        }

        return ReportAccess.CanRun(user, report) ? TypedResults.Ok(ReportDetails.From(report)) : TypedResults.Forbid();
    }

    private static async Task<IResult> RenderReport(
        string key,
        RenderRequest request,
        IReportCatalog catalog,
        ReportGenerator generator,
        ClaimsPrincipal user,
        HttpContext httpContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var report = catalog.Find(key);
        if (report is null)
        {
            return ReportNotFound(key);
        }

        if (!ReportAccess.CanRun(user, report))
        {
            return TypedResults.Forbid();
        }

        if (request.Format is not { } format || !report.Supports(format))
        {
            var supported = string.Join(", ", report.Formats.Select(f => JsonSerializer.Serialize(f).Trim('"')));
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["format"] = [$"Choose one of: {supported}."],
            });
        }

        var binding = ParameterBinder.Bind(report, request.Parameters);
        if (!binding.IsValid)
        {
            return TypedResults.ValidationProblem(binding.Errors.ToDictionary(e => e.Key, e => e.Value));
        }

        var userName = UserName(user);
        var document = await generator.GenerateAsync(report, format, binding.Parameters, userName, cancellationToken);

        LogRendered(
            loggerFactory.CreateLogger("Docugen.Audit"),
            userName,
            report.Key,
            format,
            document.RowCount,
            binding.Parameters.ToDictionary(p => p.Name, p => p.Value));

        httpContext.Response.Headers["X-Row-Count"] = document.RowCount.ToString(CultureInfo.InvariantCulture);
        return TypedResults.File(document.Content, document.ContentType, document.FileName);
    }

    private static ProblemHttpResult ReportNotFound(string key) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Report not found", detail: $"There is no report named '{key}'.");

    private static string UserName(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username")
        ?? user.Identity?.Name
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? "(unknown)";

    [LoggerMessage(Level = LogLevel.Information, Message = "AUDIT {User} rendered {ReportKey} as {Format}: {RowCount} rows, parameters {@Parameters}")]
    private static partial void LogRendered(ILogger logger, string user, string reportKey, OutputFormat format, long rowCount, Dictionary<string, object?> parameters);
}
