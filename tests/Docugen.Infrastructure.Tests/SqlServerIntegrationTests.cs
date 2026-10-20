using Docugen.Core;
using Docugen.Core.Exports;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Catalog;
using Docugen.Infrastructure.Data;
using Docugen.Infrastructure.Exports;
using Docugen.Infrastructure.Rendering;
using Docugen.Infrastructure.Rendering.Pdf;
using Docugen.Infrastructure.Store;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.MsSql;

namespace Docugen.Infrastructure.Tests;

/// <summary>
/// Runs the store scripts, the sample reports and the export job store against a real SQL Server, and checks the
/// results match the Sqlite demo. Uses the server in DOCUGEN_TEST_SQLSERVER when set (e.g. LocalDB:
/// <c>Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True</c>), otherwise SQL Server
/// in Docker. Skipped when neither is available; CI always runs them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerIntegrationTests : IAsyncLifetime
{
    private readonly string _database = $"DocugenTest_{Guid.NewGuid():N}";
    private MsSqlContainer? _container;
    private string? _server;
    private string _connectionString = "";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable("DOCUGEN_TEST_SQLSERVER");
        if (string.IsNullOrWhiteSpace(_server))
        {
            try
            {
                _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await _container.StartAsync(Cancel);
                _server = _container.GetConnectionString();
            }
            catch (DockerUnavailableException)
            {
                _server = null;
                return;
            }
        }

        await ExecuteAsync(_server, $"CREATE DATABASE {_database}");
        _connectionString = new SqlConnectionStringBuilder(_server) { InitialCatalog = _database, Pooling = false }.ConnectionString;

        // Sales data and the store share one database here; in production they are separate.
        foreach (var script in new[] { "samples/sqlserver/seed.sql", "db/sqlserver/docugen-store.sql", "samples/sqlserver/sample-reports.sql" })
        {
            await ExecuteAsync(_connectionString, await File.ReadAllTextAsync(Path.Combine(TestSupport.RepoRoot, script), Cancel));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (_server is not null)
        {
            await ExecuteAsync(_server, $"ALTER DATABASE {_database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {_database}");
        }
    }

    [Theory]
    [InlineData(OutputFormat.Csv)]
    [InlineData(OutputFormat.Xlsx)]
    [InlineData(OutputFormat.Pdf)]
    public async Task Sample_report_returns_the_same_rows_on_sql_server_and_sqlite(OutputFormat format)
    {
        SkipWithoutDocker();
        BoundParameter[] parameters =
        [
            new("FromDate", ParameterType.Date, new DateOnly(2026, 2, 1)),
            new("ToDate", ParameterType.Date, new DateOnly(2026, 4, 30)),
            new("Region", ParameterType.String, "East"),
        ];

        var sqlServer = Options("SqlServer", _connectionString, _connectionString);
        var sqlite = TestSupport.StoreOptions();
        var sqlServerRows = await GenerateAsync(sqlServer, format, parameters);
        var sqliteRows = await GenerateAsync(sqlite, format, parameters);

        Assert.True(sqlServerRows > 0);
        Assert.Equal(sqliteRows, sqlServerRows);
    }

    [Fact]
    public async Task Export_log_round_trips_on_sql_server()
    {
        SkipWithoutDocker();
        var log = new SqlExportLog(new StoreDatabase(Options("SqlServer", _connectionString, _connectionString), new TestHostEnvironment(TestSupport.RepoRoot)));
        var started = DateTimeOffset.UtcNow;
        var record = new ExportRecord { Id = Guid.CreateVersion7(), ReportKey = "sales-orders", Format = OutputFormat.Xlsx, Parameters = """{"FromDate":"2026-01-01"}""", CallerId = "alice-id", RequestedBy = "alice", StartedAt = started };

        await log.CreateAsync(record, [new ExportEvent(record.Id, started, LogLevel.Information, "Started", "started")], Cancel);
        var succeeded = record with
        {
            Status = ExportStatus.Succeeded,
            RowsExported = 42,
            TotalRows = 42,
            FileName = "sales.xlsx",
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileSize = 1234,
            CompletedAt = started.AddSeconds(2),
            DurationMs = 2000,
        };
        await log.SaveAsync(succeeded, [new ExportEvent(record.Id, started.AddSeconds(2), LogLevel.Information, "Sent", "sent")], Cancel);

        // A save without a prior create (the first write failed) still records the export.
        var orphan = record with { Id = Guid.CreateVersion7(), Status = ExportStatus.Failed, ErrorCode = "INTERNAL" };
        await log.SaveAsync(orphan, [], Cancel);

        var found = await log.FindAsync(record.Id, Cancel);
        Assert.Equal((ExportStatus.Succeeded, 42L, 1234L, 2000L), (found!.Status, found.RowsExported, found.FileSize!.Value, found.DurationMs!.Value));
        Assert.Equal(OutputFormat.Xlsx, found.Format);
        Assert.Equal(["Started", "Sent"], (await log.GetEventsAsync(record.Id, Cancel)).Select(e => e.EventType));
        Assert.Equal(ExportStatus.Failed, (await log.FindAsync(orphan.Id, Cancel))!.Status);
    }
    private void SkipWithoutDocker()
    {
        if (_server is null)
        {
            Assert.Skip("Docker is not available and DOCUGEN_TEST_SQLSERVER is not set.");
        }
    }

    private static IOptions<DocugenOptions> Options(string provider, string store, string sales) => TestSupport.Options(o =>
    {
        o.Store = new DataSourceOptions { Provider = provider, ConnectionString = store };
        o.DataSources["SalesDb"] = new DataSourceOptions { Provider = provider, ConnectionString = sales };
    });

    private static async Task<long> GenerateAsync(IOptions<DocugenOptions> options, OutputFormat format, BoundParameter[] parameters)
    {
        var environment = new TestHostEnvironment(TestSupport.RepoRoot);
        var templates = new LiquidTemplateEngine();
        var catalog = new SqlReportCatalog(new StoreDatabase(options, environment), options, templates, TimeProvider.System, NullLogger<SqlReportCatalog>.Instance);
        await catalog.RefreshAsync(Cancel);
        var report = catalog.Find("sales-orders") ?? throw new InvalidOperationException("sales-orders did not load.");
        var dataSource = new SqlReportDataSource(new DbConnectionFactory(options, environment), options);

        await using var converter = new ChromiumPdfConverter(options);
        IDocumentRenderer[] renderers =
        [
            new CsvRenderer(options),
            new ExcelRenderer(),
            new PdfRenderer(templates, converter, catalog, options),
        ];

        var generator = new ReportGenerator(dataSource, renderers, options, TimeProvider.System, NullLogger<ReportGenerator>.Instance);
        using var output = new MemoryStream();
        var result = await generator.GenerateAsync(report, format, parameters, "integration-test", output, progress: null, Cancel);
        Assert.True(output.Length > 0);
        return result.RowCount;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancel);
    }
}
