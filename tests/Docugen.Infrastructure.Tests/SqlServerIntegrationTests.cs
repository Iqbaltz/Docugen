using Docugen.Core;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Data;
using Docugen.Infrastructure.Rendering;
using Docugen.Infrastructure.Rendering.Pdf;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace Docugen.Infrastructure.Tests;

/// <summary>
/// Runs the sample reports against a real SQL Server in Docker and checks the results match the Sqlite demo data.
/// Skipped when Docker is not available (it always runs in CI).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerIntegrationTests : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (DockerUnavailableException)
        {
            _container = null;
            return;
        }

        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(TestSupport.RepoRoot, "samples", "sqlserver", "seed.sql"), TestContext.Current.CancellationToken);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;

    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Xlsx)]
    [InlineData(OutputFormat.Pdf)]
    public async Task Sample_report_returns_the_same_rows_on_sql_server_and_sqlite(OutputFormat format)
    {
        if (_container is null)
        {
            Assert.Skip("Docker is not available.");
        }

        BoundParameter[] parameters =
        [
            new("FromDate", ParameterType.Date, new DateOnly(2026, 2, 1)),
            new("ToDate", ParameterType.Date, new DateOnly(2026, 4, 30)),
            new("Region", ParameterType.String, "East"),
        ];

        var sqlServerRows = await GenerateAsync("SqlServer", _container.GetConnectionString(), format, parameters);
        var sqliteRows = await GenerateAsync("Sqlite", TestSupport.CreateDemoDatabase(), format, parameters);

        Assert.True(sqlServerRows > 0);
        Assert.Equal(sqliteRows, sqlServerRows);
    }

    private static async Task<long> GenerateAsync(string provider, string connectionString, OutputFormat format, BoundParameter[] parameters)
    {
        var options = TestSupport.Options(o => o.DataSources["SalesDb"] = new() { Provider = provider, ConnectionString = connectionString });
        var report = FileReportCatalog.Load(TestSupport.ReportsPath, options.Value, new LiquidTemplateEngine(), NullLogger.Instance)["sales-orders"];
        var dataSource = new SqlReportDataSource(new DbConnectionFactory(options, new TestHostEnvironment(TestSupport.RepoRoot)), options);

        await using var converter = new ChromiumPdfConverter(options);
        IDocumentRenderer[] renderers =
        [
            new CsvRenderer(options),
            new ExcelRenderer(),
            new PdfRenderer(new LiquidTemplateEngine(), converter, new ReportAssets(Path.Combine(TestSupport.ReportsPath, "_assets")), options),
        ];

        var generator = new ReportGenerator(dataSource, renderers, options, TimeProvider.System, NullLogger<ReportGenerator>.Instance);
        await using var document = await generator.GenerateAsync(report, format, parameters, "integration-test", TestContext.Current.CancellationToken);
        Assert.True(document.Content.Length > 0);
        return document.RowCount;
    }
}
