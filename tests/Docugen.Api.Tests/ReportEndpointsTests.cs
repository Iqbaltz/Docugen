using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Docugen.Api.Tests;

public sealed class ReportEndpointsTests(DocugenApiFactory factory) : IClassFixture<DocugenApiFactory>
{
    private static readonly object SalesParameters = new { FromDate = "2026-01-01", ToDate = "2026-03-31" };

    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requires_authentication()
    {
        var response = await factory.CreateClient(user: null).GetAsync("/api/reports", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Lists_only_the_reports_the_user_may_run()
    {
        var sales = await factory.CreateClient("sam", "Sales").GetFromJsonAsync<JsonElement[]>("/api/reports", Cancel);
        var other = await factory.CreateClient("olga", "Warehouse").GetFromJsonAsync<JsonElement[]>("/api/reports", Cancel);

        Assert.Equal(["customer-list", "sales-orders"], sales!.Select(r => r.GetProperty("key").GetString()).Order());
        Assert.Equal(["customer-list"], other!.Select(r => r.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task Describes_a_report_without_exposing_its_sql()
    {
        var json = await factory.CreateClient("sam", "Sales").GetStringAsync("/api/reports/sales-orders", Cancel);
        var report = JsonDocument.Parse(json).RootElement;

        Assert.Equal("Sales Orders", report.GetProperty("name").GetString());
        Assert.Equal(["FromDate", "ToDate", "Region"], report.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("date", report.GetProperty("parameters")[0].GetProperty("type").GetString());
        Assert.DoesNotContain("FROM Orders", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@FromDate", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renders_csv_with_a_download_name_and_row_count()
    {
        var response = await RenderAsync("sales-orders", "csv", SalesParameters);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("sales-orders-20260101-20260331.csv", response.Content.Headers.ContentDisposition!.FileNameStar);

        var rowCount = int.Parse(response.Headers.GetValues("X-Row-Count").Single());
        var lines = (await response.Content.ReadAsStringAsync(Cancel)).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(rowCount > 0);
        Assert.Equal(rowCount + 1, lines.Length);
        Assert.Equal("Order Date,Order No.,Customer,Region,Qty,Total", lines[0]);
    }

    [Fact]
    public async Task Filters_by_optional_parameters()
    {
        var all = await RenderAsync("sales-orders", "csv", SalesParameters);
        var east = await RenderAsync("sales-orders", "csv", new { FromDate = "2026-01-01", ToDate = "2026-03-31", Region = "East" });

        var eastLines = (await east.Content.ReadAsStringAsync(Cancel)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.NotEmpty(eastLines);
        Assert.All(eastLines, line => Assert.Contains(",East,", line));
        Assert.True(eastLines.Count < int.Parse(all.Headers.GetValues("X-Row-Count").Single()));
    }

    [Fact]
    public async Task Renders_xlsx()
    {
        var response = await RenderAsync("sales-orders", "xlsx", SalesParameters);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("PK"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync(Cancel))[..2]);
    }

    [Fact]
    public async Task Renders_pdf_from_the_report_template()
    {
        var response = await RenderAsync("sales-orders", "pdf", SalesParameters);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync(Cancel))[..4]);
    }

    [Fact]
    public async Task Rejects_invalid_parameters_with_field_errors()
    {
        var response = await RenderAsync("sales-orders", "csv", new { ToDate = "31/03/2026", Region = "Moon", Extra = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Cancel)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("FromDate", out _));
        Assert.True(errors.TryGetProperty("ToDate", out _));
        Assert.True(errors.TryGetProperty("Region", out _));
        // Unknown names are reported as the caller sent them (PostAsJsonAsync camel-cases "Extra").
        Assert.True(errors.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task Rejects_a_missing_or_unknown_format()
    {
        var missing = await factory.CreateClient("sam", "Sales").PostAsync(
            "/api/reports/sales-orders/render",
            new StringContent("""{ "parameters": {} }""", Encoding.UTF8, "application/json"),
            Cancel);
        var unknown = await RenderAsync("sales-orders", "docx", SalesParameters);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Returns_404_for_unknown_reports_and_403_without_the_role()
    {
        var unknown = await RenderAsync("nope", "csv", new { });
        var forbidden = await RenderAsync("sales-orders", "csv", SalesParameters, roles: "Warehouse");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Returns_422_when_a_pdf_would_have_too_many_rows()
    {
        // customer-list has 12 rows; the test host caps PDFs at 10.
        var response = await RenderAsync("customer-list", "pdf", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Contains("xlsx or csv", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reports_health()
    {
        var response = await factory.CreateClient(user: null).GetAsync("/health", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private Task<HttpResponseMessage> RenderAsync(string key, string format, object parameters, string roles = "Sales") =>
        factory.CreateClient("sam@example.com", roles).PostAsJsonAsync($"/api/reports/{key}/render", new { format, parameters }, Cancel);
}
