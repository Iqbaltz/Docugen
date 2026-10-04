using Docugen.Core;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Rendering.Pdf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Docugen.Infrastructure.Tests;

public sealed class FileReportCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docugen-catalog-").FullName;
    private readonly DocugenOptions _options = TestSupport.Options(o => o.DataSources["SalesDb"] = new() { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" }).Value;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Loads_the_sample_reports_in_the_repository()
    {
        var reports = FileReportCatalog.Load(TestSupport.ReportsPath, _options, new LiquidTemplateEngine(), NullLogger.Instance);

        Assert.Contains("sales-orders", reports.Keys);
        Assert.Contains("customer-list", reports.Keys);
        var salesOrders = reports["sales-orders"];
        Assert.Contains("@FromDate", salesOrders.Query);
        Assert.NotNull(salesOrders.PdfTemplateSource);
        Assert.Null(reports["customer-list"].PdfTemplateSource);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        WriteReport("broken", """
            {
              "key": "broken",
              "name": "Broken",
              "dataSource": "Nowhere",
              "parameters": [ { "name": "FromDate", "type": "date", "default": "not-a-date" } ],
              "pdf": { "template": "template.html", "pageSize": "B9" }
            }
            """,
            query: "SELECT * FROM Orders WHERE OrderDate >= @FromDate AND Region = @Region",
            template: "{% if %}");
        WriteReport("wrong-folder", """{ "key": "other", "name": "X", "dataSource": "SalesDb", "query": "SELECT 1" }""");
        WriteReport("typo", """{ "key": "typo", "name": "X", "dataSource": "SalesDb", "query": "SELECT 1", "formts": ["pdf"] }""");

        var error = Assert.Throws<ReportCatalogException>(Load);

        Assert.Contains(error.Errors, e => e.StartsWith("broken: dataSource 'Nowhere'", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("broken: query uses @Region", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("broken: default for parameter 'FromDate'", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("broken: pdf.pageSize 'B9'", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("broken: pdf template does not compile", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("wrong-folder: key 'other' must match", StringComparison.Ordinal));
        Assert.Contains(error.Errors, e => e.StartsWith("typo: report.json is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_templates_outside_the_report_folder()
    {
        WriteReport("escape", """
            { "key": "escape", "name": "X", "dataSource": "SalesDb", "query": "SELECT 1", "pdf": { "template": "../../secret.html" } }
            """);

        var error = Assert.Throws<ReportCatalogException>(Load);

        Assert.Contains(error.Errors, e => e.Contains("must be inside the report folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_a_query()
    {
        WriteReport("empty", """{ "key": "empty", "name": "X", "dataSource": "SalesDb" }""");

        var error = Assert.Throws<ReportCatalogException>(Load);

        Assert.Contains(error.Errors, e => e.Contains("no query", StringComparison.Ordinal));
    }

    [Fact]
    public void Skips_folders_starting_with_an_underscore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "_assets"));

        Assert.Empty(Load());
    }

    private Dictionary<string, Core.Reports.ReportDefinition> Load() =>
        FileReportCatalog.Load(_root, _options, new LiquidTemplateEngine(), NullLogger.Instance);

    private void WriteReport(string folder, string json, string? query = null, string? template = null)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        File.WriteAllText(Path.Combine(path, "report.json"), json);
        if (query is not null)
        {
            File.WriteAllText(Path.Combine(path, "query.sql"), query);
        }

        if (template is not null)
        {
            File.WriteAllText(Path.Combine(path, "template.html"), template);
        }
    }
}
