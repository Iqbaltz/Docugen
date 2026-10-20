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
        Assert.Equal(["FromDate", "ToDate", "Region"], salesOrders.Parameters.Select(p => p.Name));
        Assert.Equal((Core.Reports.ParameterType.Date, true, "From"), (salesOrders.Parameters[0].Type, salesOrders.Parameters[0].Required, salesOrders.Parameters[0].Label));
        Assert.Equal(["North", "South", "East", "West"], salesOrders.Parameters[2].AllowedValues!);
        Assert.Equal(["OrderDate", "OrderNumber", "Customer", "Region", "Quantity", "Total"], salesOrders.Columns.Select(c => c.Field));
        Assert.Equal(("Order Date", "dd-mmm-yyyy", 14.0), (salesOrders.Columns[0].Header, salesOrders.Columns[0].Format, salesOrders.Columns[0].Width!.Value));
        Assert.NotNull(salesOrders.PdfTemplateSource);
        Assert.Equal(("A4", 5000, "en-US"), (salesOrders.Pdf.PageSize, salesOrders.Pdf.MaxRows!.Value, salesOrders.Culture));

        var customers = Assert.IsType<Core.Reports.ReportDefinition>(_catalog.Find("customer-list"));
        Assert.Empty(customers.Parameters);
        Assert.Empty(customers.Columns);
        Assert.Null(customers.PdfTemplateSource);
        Assert.Null(customers.CountQuery);
        Assert.Equal("A4", customers.Pdf.PageSize);

        var assets = ((IReportAssets)_catalog).Values;
        Assert.StartsWith("data:image/svg+xml;base64,", (string)assets["logo_svg"]!);
        Assert.Contains("table.data", (string)assets["report_css"]!);
    }

    [Fact]
    public async Task Turns_default_values_into_the_values_callers_would_send()
    {
        await TestSupport.ExecuteAsync(StoreConnection, """
            INSERT INTO Reports (ReportKey, Name, DataSource, Query)
            VALUES ('defaults', 'Defaults', 'SalesDb', 'SELECT @Top, @Active, @Ids, @Region, @Since');
            INSERT INTO ReportParameters (ReportKey, Name, SortOrder, Type, DefaultValue) VALUES
                ('defaults', 'Top', 1, 'int', '100'),
                ('defaults', 'Active', 2, 'bool', 'true'),
                ('defaults', 'Ids', 3, 'int[]', '1, 2,3'),
                ('defaults', 'Region', 4, 'string', 'North'),
                ('defaults', 'Since', 5, 'date', '2026-01-01');
            """);

        await _catalog.RefreshAsync(Cancel);

        var report = Assert.IsType<Core.Reports.ReportDefinition>(_catalog.Find("defaults"));
        Assert.Equal(["100", "true", """["1","2","3"]""", "\"North\"", "\"2026-01-01\""], report.Parameters.Select(p => p.Default!.Value.GetRawText()));
        var bound = Core.Parameters.ParameterBinder.Bind(report, null);
        Assert.True(bound.IsValid);
        Assert.Equal<object?>([100, true, new[] { 1, 2, 3 }, "North", new DateOnly(2026, 1, 1)], bound.Parameters.Select(p => p.Value));
    }

    [Fact]
    public async Task Leaves_out_invalid_reports_and_records_why()
    {
        await TestSupport.ExecuteAsync(StoreConnection, """
            INSERT INTO Reports (ReportKey, Name, DataSource, Query)
            VALUES ('broken', 'Broken', 'Nowhere', 'SELECT * FROM Orders WHERE OrderDate >= @FromDate AND Region = @Region');
            INSERT INTO ReportParameters (ReportKey, Name, Type, DefaultValue) VALUES ('broken', 'FromDate', 'date', 'not-a-date');
            INSERT INTO ReportPdfSettings (ReportKey, Template, PageSize) VALUES ('broken', '{% if %}', 'B9');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query, Formats) VALUES ('bad-format', 'X', 'SalesDb', 'SELECT 1', 'pdf,docx');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query, CountQuery) VALUES ('bad-count', 'X', 'SalesDb', 'SELECT 1', 'SELECT COUNT(*) WHERE @Nope = 1');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query) VALUES ('bad-type', 'X', 'SalesDb', 'SELECT @When');
            INSERT INTO ReportParameters (ReportKey, Name, Type) VALUES ('bad-type', 'When', 'timestamp');
            INSERT INTO Reports (ReportKey, Name, DataSource, Query) VALUES ('Upper Case', 'X', 'SalesDb', 'SELECT 1');
            """);

        await _catalog.RefreshAsync(Cancel);

        Assert.NotNull(_catalog.Find("sales-orders"));
        Assert.Null(_catalog.Find("broken"));
        Assert.Null(_catalog.Find("bad-format"));
        Assert.Null(_catalog.Find("bad-type"));

        var validation = (await TestSupport.QueryAsync(StoreConnection, "SELECT ReportKey, IsValid, Errors FROM ReportValidation"))
            .ToDictionary(r => (string)r["ReportKey"]!, r => (Valid: (long)r["IsValid"]! == 1, Errors: (string?)r["Errors"] ?? ""));

        Assert.True(validation["sales-orders"].Valid);
        var broken = validation["broken"].Errors;
        Assert.False(validation["broken"].Valid);
        Assert.Contains("DataSource 'Nowhere'", broken);
        Assert.Contains("Query uses @Region but ReportParameters has no row for it", broken);
        Assert.Contains("ReportParameters: DefaultValue for 'FromDate'", broken);
        Assert.Contains("ReportPdfSettings.PageSize 'B9'", broken);
        Assert.Contains("ReportPdfSettings.Template does not compile", broken);
        Assert.Contains("Formats: 'docx'", validation["bad-format"].Errors);
        Assert.Contains("CountQuery uses @Nope", validation["bad-count"].Errors);
        Assert.Contains("ReportParameters: 'When' has type 'timestamp'", validation["bad-type"].Errors);
        Assert.Contains("must be lower-case", validation["Upper Case"].Errors);
    }

    [Fact]
    public async Task Picks_up_changes_to_any_of_a_reports_rows_on_the_next_refresh()
    {
        await _catalog.RefreshAsync(Cancel);

        await TestSupport.ExecuteAsync(StoreConnection, """
            UPDATE Reports SET Name = 'Customers (renamed)' WHERE ReportKey = 'customer-list';
            INSERT INTO ReportColumns (ReportKey, Field, SortOrder, Header) VALUES ('customer-list', 'Customer', 1, 'Name');
            UPDATE ReportAssets SET TextContent = 'body { color: red; }' WHERE FileName = 'report.css';
            """);
        await _catalog.RefreshAsync(Cancel);

        var customers = _catalog.Find("customer-list")!;
        Assert.Equal("Customers (renamed)", customers.Name);
        Assert.Equal(["Name"], customers.Columns.Select(c => c.Header));
        Assert.Equal("body { color: red; }", ((IReportAssets)_catalog).Values["report_css"]);

        await TestSupport.ExecuteAsync(StoreConnection, """
            UPDATE ReportParameters SET Label = 'Start date' WHERE ReportKey = 'sales-orders' AND Name = 'FromDate';
            UPDATE ReportPdfSettings SET Landscape = 1 WHERE ReportKey = 'sales-orders';
            """);
        await _catalog.RefreshAsync(Cancel);

        var salesOrders = _catalog.Find("sales-orders")!;
        Assert.Equal("Start date", salesOrders.Parameters[0].Label);
        Assert.True(salesOrders.Pdf.Landscape);

        await TestSupport.ExecuteAsync(StoreConnection, "UPDATE Reports SET IsActive = 0 WHERE ReportKey = 'sales-orders';");
        await _catalog.RefreshAsync(Cancel);

        Assert.Null(_catalog.Find("sales-orders"));
        var keys = (await TestSupport.QueryAsync(StoreConnection, "SELECT ReportKey FROM ReportValidation")).Select(r => r["ReportKey"]);
        Assert.Equal(["customer-list"], keys);
    }
}