using Docugen.Core;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Rendering.Pdf;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Tests;

public sealed class SqlReportCatalogTests
{
    private readonly IOptions<DocugenOptions> _options = TestSupport.StoreOptions();
    private readonly SqlReportCatalog _catalog;

    public SqlReportCatalogTests()
    {
        var store = new StoreDatabase(_options, new TestHostEnvironment(TestSupport.RepoRoot));
        _catalog = new SqlReportCatalog(store, _options, new LiquidTemplateEngine(), TimeProvider.System, NullLogger<SqlReportCatalog>.Instance);
    }

    private string StoreConnection => _options.Value.Store.ConnectionString;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Loads_the_sample_reports_and_assets()
    {
        await _catalog.RefreshAsync(Cancel);

        var salesOrders = Assert.IsType<Core.Reports.ReportDefinition>(_catalog.Find("sales-orders"));
        Assert.Contains("@FromDate", salesOrders.Query);
        Assert.Contains("COUNT(*)", salesOrders.CountQuery);
        Assert.Equal(["Sales", "Finance"], salesOrders.AllowedRoles);
        Assert.Equal(["FromDate", "ToDate", "Region"], salesOrders.Parameters.Select(p => p.Name));
        Assert.Equal(6, salesOrders.Columns.Count);
        Assert.NotNull(salesOrders.PdfTemplateSource);
        Assert.Equal("A4", salesOrders.Pdf.PageSize);

        var customers = Assert.IsType<Core.Reports.ReportDefinition>(_catalog.Find("customer-list"));
        Assert.Empty(customers.AllowedRoles);
        Assert.Null(customers.PdfTemplateSource);
        Assert.Null(customers.CountQuery);

        var assets = ((IReportAssets)_catalog).Values;
        Assert.StartsWith("data:image/svg+xml;base64,", (string)assets["logo_svg"]!);
        Assert.Contains("table.data", (string)assets["report_css"]!);
    }

    [Fact]
    public async Task Leaves_out_invalid_reports_and_records_why()
    {
        await TestSupport.ExecuteAsync(StoreConnection, """
            INSERT INTO Reports (ReportKey, Name, DataSource, Query, Parameters, PdfTemplate, PdfPageSize)
            VALUES ('broken', 'Broken', 'Nowhere',
                    'SELECT * FROM Orders WHERE OrderDate >= @FromDate AND Region = @Region',
                    '[{"name":"FromDate","type":"date","default":"not-a-date"}]',
                    '{% if %}', 'B9');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query, Formats, CountQuery)
            VALUES ('bad-format', 'X', 'SalesDb', 'SELECT 1', 'pdf,docx', 'SELECT COUNT(*) WHERE @Nope = 1');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query, Columns)
            VALUES ('typo', 'X', 'SalesDb', 'SELECT 1', '[{"field":"A","heder":"B"}]');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query) VALUES ('Upper Case', 'X', 'SalesDb', 'SELECT 1');
            """);

        await _catalog.RefreshAsync(Cancel);

        Assert.NotNull(_catalog.Find("sales-orders"));
        Assert.Null(_catalog.Find("broken"));
        Assert.Null(_catalog.Find("bad-format"));
        Assert.Null(_catalog.Find("typo"));

        var validation = (await TestSupport.QueryAsync(StoreConnection, "SELECT ReportKey, IsValid, Errors FROM ReportValidation"))
            .ToDictionary(r => (string)r["ReportKey"]!, r => (Valid: (long)r["IsValid"]! == 1, Errors: (string?)r["Errors"] ?? ""));

        Assert.True(validation["sales-orders"].Valid);
        var broken = validation["broken"].Errors;
        Assert.False(validation["broken"].Valid);
        Assert.Contains("DataSource 'Nowhere'", broken);
        Assert.Contains("Query uses @Region", broken);
        Assert.Contains("default for 'FromDate'", broken);
        Assert.Contains("PdfPageSize 'B9'", broken);
        Assert.Contains("PdfTemplate does not compile", broken);
        Assert.Contains("Formats: 'docx'", validation["bad-format"].Errors);
        Assert.Contains("Columns is not a valid JSON array", validation["typo"].Errors);
        Assert.Contains("must be lower-case", validation["Upper Case"].Errors);
    }

    [Fact]
    public async Task Picks_up_changes_on_the_next_refresh()
    {
        await _catalog.RefreshAsync(Cancel);

        await TestSupport.ExecuteAsync(StoreConnection, """
            UPDATE Reports SET Name = 'Customers (renamed)' WHERE ReportKey = 'customer-list';
            UPDATE Reports SET IsActive = 0 WHERE ReportKey = 'sales-orders';
            UPDATE ReportAssets SET TextContent = 'body { color: red; }' WHERE FileName = 'report.css';
            """);
        await _catalog.RefreshAsync(Cancel);

        Assert.Equal("Customers (renamed)", _catalog.Find("customer-list")!.Name);
        Assert.Null(_catalog.Find("sales-orders"));
        Assert.Equal("body { color: red; }", ((IReportAssets)_catalog).Values["report_css"]);
        var keys = (await TestSupport.QueryAsync(StoreConnection, "SELECT ReportKey FROM ReportValidation")).Select(r => r["ReportKey"]);
        Assert.Equal(["customer-list"], keys);
    }
}
