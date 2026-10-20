using Docugen.Core;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Data;

namespace Docugen.Infrastructure.Tests;

public sealed class SqlReportDataSourceTests
{
    private const string OrdersQuery = """
        SELECT o.OrderDate, o.OrderNumber, r.Name AS Region, o.Total
        FROM Orders o
        JOIN Customers c ON c.Id = o.CustomerId
        JOIN Regions r ON r.Id = c.RegionId
        WHERE o.OrderDate >= @FromDate AND o.OrderDate <= @ToDate AND (@Region IS NULL OR r.Name = @Region)
        ORDER BY o.OrderDate, o.OrderNumber
        """;

    private readonly SqlReportDataSource _dataSource;

    public SqlReportDataSourceTests()
    {
        var connectionString = TestSupport.CreateDemoDatabase();
        var options = TestSupport.Options(o => o.DataSources["SalesDb"] = new() { Provider = "Sqlite", ConnectionString = connectionString });
        _dataSource = new SqlReportDataSource(new DbConnectionFactory(options, new TestHostEnvironment(TestSupport.RepoRoot)), options);
    }

    [Fact]
    public async Task Streams_rows_filtered_by_typed_parameters()
    {
        var report = TestSupport.Report() with
        {
            Query = OrdersQuery,
            Columns = [new ColumnDefinition { Field = "OrderNumber", Header = "Order No." }, new ColumnDefinition { Field = "OrderDate" }, new ColumnDefinition { Field = "Region" }],
        };

        var rows = await ReadAsync(report, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), "North");

        Assert.NotEmpty(rows.Rows);
        Assert.Equal(["Order No.", "OrderDate", "Region"], rows.Headers);
        Assert.All(rows.Rows, r =>
        {
            Assert.StartsWith("SO-", (string)r[0]!);
            var date = Assert.IsType<DateTime>(r[1]);
            Assert.InRange(date, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31));
            Assert.Equal("North", r[2]);
        });
    }

    [Fact]
    public async Task Includes_rows_on_the_boundary_dates()
    {
        var report = TestSupport.Report() with { Query = OrdersQuery };

        var all = await ReadAsync(report, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null);
        var firstDate = DateOnly.FromDateTime((DateTime)all.Rows[0][0]!);
        var sameDay = await ReadAsync(report, firstDate, firstDate, null);

        Assert.Equal(240, all.Rows.Count);
        Assert.NotEmpty(sameDay.Rows);
    }

    [Fact]
    public async Task Returns_every_column_when_the_report_lists_none()
    {
        var report = TestSupport.Report() with { Query = "SELECT Id, Name FROM Regions ORDER BY Id" };

        await using var data = await _dataSource.ExecuteAsync(report, [], TestContext.Current.CancellationToken);

        Assert.Equal(["Id", "Name"], data.Columns.Select(c => c.Header));
    }

    [Fact]
    public async Task Fails_clearly_when_a_listed_column_is_missing()
    {
        var report = TestSupport.Report() with
        {
            Query = "SELECT Id FROM Regions",
            Columns = [new ColumnDefinition { Field = "Nope" }],
        };

        var error = await Assert.ThrowsAsync<ReportDefinitionMismatchException>(() =>
            _dataSource.ExecuteAsync(report, [], TestContext.Current.CancellationToken));

        Assert.Contains("'Nope'", error.Message);
    }

    [Fact]
    public async Task Counts_rows_with_the_count_query_and_the_same_parameters()
    {
        var report = TestSupport.Report() with
        {
            Query = OrdersQuery,
            CountQuery = "SELECT COUNT(*) FROM Orders o JOIN Customers c ON c.Id = o.CustomerId JOIN Regions r ON r.Id = c.RegionId WHERE o.OrderDate >= @FromDate AND o.OrderDate <= @ToDate AND (@Region IS NULL OR r.Name = @Region)",
        };
        BoundParameter[] parameters =
        [
            new("FromDate", ParameterType.Date, new DateOnly(2026, 1, 1)),
            new("ToDate", ParameterType.Date, new DateOnly(2026, 3, 31)),
            new("Region", ParameterType.String, "East"),
        ];

        var count = await _dataSource.CountAsync(report, parameters, TestContext.Current.CancellationToken);
        var rows = await ReadAsync(report, new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), "East");

        Assert.Equal(rows.Rows.Count, count);
        Assert.True(count > 0);
    }

    [Fact]
    public async Task Passes_array_parameters_as_json()
    {
        var report = TestSupport.Report() with { Query = "SELECT Name FROM Regions WHERE Id IN (SELECT value FROM json_each(@Ids)) ORDER BY Id" };

        await using var data = await _dataSource.ExecuteAsync(report, [new BoundParameter("Ids", ParameterType.IntArray, new[] { 2, 4 })], TestContext.Current.CancellationToken);
        var names = new List<object?>();
        await foreach (var row in data.ReadRowsAsync(TestContext.Current.CancellationToken))
        {
            names.Add(row[0]);
        }

        Assert.Equal(["South", "West"], names);
    }

    private async Task<(List<string> Headers, List<object?[]> Rows)> ReadAsync(ReportDefinition report, DateOnly from, DateOnly to, string? region)
    {
        BoundParameter[] parameters =
        [
            new("FromDate", ParameterType.Date, from),
            new("ToDate", ParameterType.Date, to),
            new("Region", ParameterType.String, region),
        ];

        await using var data = await _dataSource.ExecuteAsync(report, parameters, TestContext.Current.CancellationToken);
        var rows = new List<object?[]>();
        await foreach (var row in data.ReadRowsAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        return (data.Columns.Select(c => c.Header).ToList(), rows);
    }
}
