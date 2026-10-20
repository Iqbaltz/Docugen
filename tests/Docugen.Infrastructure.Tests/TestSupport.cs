using System.Globalization;
using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Tests;

internal static class TestSupport
{
    public static readonly string RepoRoot = FindRepoRoot();

    public static ReportDefinition Report(string key = "test-report") => new()
    {
        Key = key,
        Name = "Test Report",
        DataSource = "SalesDb",
        Query = "SELECT 1",
    };

    public static RenderContext Context(ReportDefinition? report = null, IReadOnlyList<BoundParameter>? parameters = null, string culture = "en-US") =>
        new(report ?? Report(), parameters ?? [], CultureInfo.GetCultureInfo(culture), new DateTimeOffset(2026, 10, 4, 9, 30, 0, TimeSpan.Zero), "tester@example.com");

    public static InMemoryReportData Data(ColumnInfo[] columns, params object?[][] rows) => new(columns, rows);

    public static IOptions<DocugenOptions> Options(Action<DocugenOptions>? configure = null)
    {
        var options = new DocugenOptions
        {
            Pdf = new PdfRenderingOptions { BrowserExecutablePath = Environment.GetEnvironmentVariable("DOCUGEN_BROWSER_PATH") },
        };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    /// <summary>Options for a Sqlite store and a Sqlite SalesDb, both freshly seeded.</summary>
    public static IOptions<DocugenOptions> StoreOptions(Action<DocugenOptions>? configure = null)
    {
        var store = CreateStoreDatabase();
        var sales = CreateDemoDatabase();
        return Options(o =>
        {
            o.Store = new DataSourceOptions { Provider = "Sqlite", ConnectionString = store };
            o.DataSources["SalesDb"] = new DataSourceOptions { Provider = "Sqlite", ConnectionString = sales };
            configure?.Invoke(o);
        });
    }

    /// <summary>Creates a Sqlite database file seeded with the demo sales data and returns its connection string.</summary>
    public static string CreateDemoDatabase() => CreateDatabase("samples/sqlite/seed.sql");

    /// <summary>Creates a Sqlite Docugen store with the sample reports and returns its connection string.</summary>
    public static string CreateStoreDatabase() => CreateDatabase("samples/sqlite/docugen-store.sql");

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        }

        return rows;
    }

    private static string CreateDatabase(string script)
    {
        var path = Path.Combine(Path.GetTempPath(), $"docugen-test-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = File.ReadAllText(Path.Combine(RepoRoot, script));
        command.ExecuteNonQuery();
        return connectionString;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Docugen.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Docugen.slnx).");
    }
}

internal sealed class TestHostEnvironment(string contentRoot) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";

    public string ApplicationName { get; set; } = "Docugen.Tests";

    public string ContentRootPath { get; set; } = contentRoot;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
