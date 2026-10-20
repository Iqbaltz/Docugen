using System.Text;
using System.Text.Json;
using Docugen.Contracts.V1;
using Docugen.Infrastructure.Exports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Health.V1;
using Microsoft.Extensions.DependencyInjection;

namespace Docugen.Api.Tests;

public sealed class GrpcServicesTests(DocugenApiFactory factory) : IClassFixture<DocugenApiFactory>
{
    private static readonly Metadata Sam = DocugenApiFactory.As("sam@example.com", "Sales");
    private static readonly object FirstQuarter = new { FromDate = "2026-01-01", ToDate = "2026-03-31" };

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private ReportService.ReportServiceClient Reports => new(factory.Channel);

    private ExportService.ExportServiceClient Exports => new(factory.Channel);

    [Fact]
    public async Task Requires_authentication()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => Reports.ListReportsAsync(new ListReportsRequest(), cancellationToken: Cancel).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task Lists_only_the_reports_the_caller_may_run()
    {
        var sales = await Reports.ListReportsAsync(new ListReportsRequest(), Sam, cancellationToken: Cancel);
        var other = await Reports.ListReportsAsync(new ListReportsRequest(), DocugenApiFactory.As("olga", "Warehouse"), cancellationToken: Cancel);

        Assert.Equal(["customer-list", "sales-orders"], sales.Reports.Select(r => r.Key).Order());
        Assert.Equal(["customer-list"], other.Reports.Select(r => r.Key));
    }

    [Fact]
    public async Task Describes_a_report_without_exposing_its_sql()
    {
        var report = await Reports.GetReportAsync(new GetReportRequest { Key = "sales-orders" }, Sam, cancellationToken: Cancel);
        var json = JsonFormatter.Default.Format(report);

        Assert.Equal("Sales Orders", report.Name);
        Assert.Equal(["FromDate", "ToDate", "Region"], report.Parameters.Select(p => p.Name));
        Assert.Equal("date", report.Parameters[0].Type);
        Assert.Equal(["North", "South", "East", "West"], report.Parameters[2].AllowedValues);
        Assert.True(report.ExactProgress);
        Assert.DoesNotContain("FROM Orders", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@FromDate", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Streams_progress_then_the_file_and_logs_the_export()
    {
        var result = await ExportAsync("sales-orders", OutputFormat.Csv, FirstQuarter);

        Assert.NotEmpty(result.Progress);
        Assert.Equal(result.Progress.Select(p => p.Percent).Order(), result.Progress.Select(p => p.Percent));
        Assert.Equal((ExportStage.Completed, 100), (result.Progress[^1].Stage, result.Progress[^1].Percent));
        Assert.Equal(result.File.RowCount, result.Progress[^1].TotalRows);

        Assert.Equal(("sales-orders-20260101-20260331.csv", "text/csv"), (result.File.FileName, result.File.ContentType));
        Assert.Equal(result.File.Size, result.Content.Length);
        var lines = Encoding.UTF8.GetString(result.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Order Date,Order No.,Customer,Region,Qty,Total", lines[0]);
        Assert.Equal(result.File.RowCount + 1, lines.Length);

        var log = factory.Services.GetRequiredService<IExportLog>();
        var id = Guid.Parse(result.File.ExportId);
        var record = await log.FindAsync(id, Cancel);
        Assert.Equal((Core.Exports.ExportStatus.Succeeded, result.File.RowCount, "sam@example.com"), (record!.Status, record.RowsExported, record.RequestedBy));
        Assert.Equal(
            ["Started", "CountingRows", "Counted", "Querying", "WritingRows", "Finalizing", "Generated", "Sent"],
            (await log.GetEventsAsync(id, Cancel)).Select(e => e.EventType));
        Assert.Empty(Directory.Exists(factory.TempPath) ? Directory.EnumerateFiles(factory.TempPath) : []);
    }

    [Fact]
    public async Task Filters_by_optional_parameters()
    {
        var all = await ExportAsync("sales-orders", OutputFormat.Csv, FirstQuarter);
        var east = await ExportAsync("sales-orders", OutputFormat.Csv, new { FromDate = "2026-01-01", ToDate = "2026-03-31", Region = "East" });

        var eastLines = Encoding.UTF8.GetString(east.Content).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.NotEmpty(eastLines);
        Assert.All(eastLines, line => Assert.Contains(",East,", line));
        Assert.True(east.File.RowCount < all.File.RowCount);
    }

    [Theory]
    [InlineData(OutputFormat.Xlsx, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "PK")]
    [InlineData(OutputFormat.Pdf, "application/pdf", "%PDF")]
    public async Task Exports_xlsx_and_pdf(OutputFormat format, string contentType, string signature)
    {
        var result = await ExportAsync("sales-orders", format, new { FromDate = "2026-01-01", ToDate = "2026-01-31" });

        Assert.Equal(contentType, result.File.ContentType);
        Assert.Equal(signature, Encoding.ASCII.GetString(result.Content, 0, signature.Length));
    }

    [Fact]
    public async Task Rejects_invalid_parameters_with_a_violation_per_field()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            ExportAsync("sales-orders", OutputFormat.Csv, new { ToDate = "31/03/2026", Region = "Moon", Extra = 1 }));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        var fields = error.GetRpcStatus()!.GetDetail<BadRequest>().FieldViolations.Select(v => v.Field);
        Assert.Equal(["parameters.Extra", "parameters.FromDate", "parameters.Region", "parameters.ToDate"], fields.Order());
        Assert.Contains("FromDate", error.Status.Detail);
    }

    [Fact]
    public async Task Rejects_a_missing_format()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => ExportAsync("sales-orders", OutputFormat.Unspecified, FirstQuarter));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Equal("format", error.GetRpcStatus()!.GetDetail<BadRequest>().FieldViolations.Single().Field);
    }

    [Fact]
    public async Task Returns_not_found_for_unknown_reports_and_permission_denied_without_the_role()
    {
        var unknown = await Assert.ThrowsAsync<RpcException>(() => ExportAsync("nope", OutputFormat.Csv, new { }));
        var forbidden = await Assert.ThrowsAsync<RpcException>(() =>
            ExportAsync("sales-orders", OutputFormat.Csv, FirstQuarter, DocugenApiFactory.As("olga", "Warehouse")));

        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, forbidden.StatusCode);
    }

    [Fact]
    public async Task Fails_a_pdf_with_too_many_rows_with_the_reason_and_export_id()
    {
        // customer-list has 12 rows and no count query; the test host caps PDFs at 10.
        var error = await Assert.ThrowsAsync<RpcException>(() => ExportAsync("customer-list", OutputFormat.Pdf, new { }));

        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Contains("xlsx or csv", error.Status.Detail);
        var info = error.GetRpcStatus()!.GetDetail<ErrorInfo>();
        Assert.Equal(("TOO_MANY_ROWS", "docugen"), (info.Reason, info.Domain));

        var record = await factory.Services.GetRequiredService<IExportLog>().FindAsync(Guid.Parse(info.Metadata["export_id"]), Cancel);
        Assert.Equal((Core.Exports.ExportStatus.Failed, "TOO_MANY_ROWS"), (record!.Status, record.ErrorCode));
    }

    [Fact]
    public async Task Logs_calls_that_run_out_of_time_as_cancelled_and_never_leaves_them_running()
    {
        var caller = DocugenApiFactory.As("deadline@example.com", "Sales");
        foreach (var deadlineMs in new[] { 1, 5, 20, 60, 150 })
        {
            var request = new ExportRequest { ReportKey = "sales-orders", Format = OutputFormat.Pdf, Parameters = Struct.Parser.ParseJson(JsonSerializer.Serialize(FirstQuarter)) };
            using var call = Exports.Export(request, caller, deadline: DateTime.UtcNow.AddMilliseconds(deadlineMs), cancellationToken: Cancel);
            var error = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                await foreach (var _ in call.ResponseStream.ReadAllAsync(Cancel))
                {
                }
            });
            // Over Kestrel this is DEADLINE_EXCEEDED; the in-memory test server aborts the stream, which reads as UNAVAILABLE.
            Assert.True(error.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Unavailable, $"{error.StatusCode}: {error.Status.Detail}");
        }

        // Calls cancelled before they reached the exporter are not logged; every other one must end as Cancelled.
        var statuses = new List<string>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            statuses = await QueryStatusesAsync("deadline@example.com");
            if (!statuses.Contains("Running"))
            {
                break;
            }

            await Task.Delay(100, Cancel);
        }

        Assert.All(statuses, status => Assert.Equal("Cancelled", status));
        Assert.Empty(Directory.Exists(factory.TempPath) ? Directory.EnumerateFiles(factory.TempPath) : []);
        Assert.Equal("application/pdf", (await ExportAsync("sales-orders", OutputFormat.Pdf, FirstQuarter, caller)).File.ContentType);
    }

    [Fact]
    public async Task Reports_health_without_a_token()
    {
        var response = await new Health.HealthClient(factory.Channel).CheckAsync(new HealthCheckRequest(), cancellationToken: Cancel);

        Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, response.Status);
    }

    private async Task<List<string>> QueryStatusesAsync(string requestedBy)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(factory.StoreConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM Exports WHERE RequestedBy = $requestedBy";
        command.Parameters.AddWithValue("$requestedBy", requestedBy);
        var statuses = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            statuses.Add(reader.GetString(0));
        }

        return statuses;
    }

    /// <summary>Calls Export and splits the stream into its progress messages, file info and content.</summary>
    private async Task<(List<ExportProgress> Progress, ExportFileInfo File, byte[] Content)> ExportAsync(string key, OutputFormat format, object parameters, Metadata? caller = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var request = new ExportRequest { ReportKey = key, Format = format, Parameters = Struct.Parser.ParseJson(JsonSerializer.Serialize(parameters)) };
        using var call = Exports.Export(request, caller ?? Sam, cancellationToken: timeout.Token);

        var progress = new List<ExportProgress>();
        ExportFileInfo? file = null;
        using var content = new MemoryStream();
        await foreach (var message in call.ResponseStream.ReadAllAsync(timeout.Token))
        {
            switch (message.PayloadCase)
            {
                case ExportResponse.PayloadOneofCase.Progress:
                    Assert.Null(file);
                    progress.Add(message.Progress);
                    break;
                case ExportResponse.PayloadOneofCase.File:
                    Assert.Null(file);
                    file = message.File;
                    break;
                default:
                    Assert.NotNull(file);
                    message.Chunk.WriteTo(content);
                    break;
            }
        }

        return (progress, file!, content.ToArray());
    }
}
