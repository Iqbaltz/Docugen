using System.Data.Common;
using Docugen.Core;
using Docugen.Infrastructure.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Store;

/// <summary>
/// Opens connections to Docugen's own database (<c>Docugen:Store</c>: report definitions and the export log) and
/// names its tables. On SQL Server the tables live in the <c>docugen</c> schema; Sqlite has no schemas, so there
/// they are unqualified.
/// </summary>
public sealed class StoreDatabase
{
    private readonly string _provider;
    private readonly string _connectionString;

    public StoreDatabase(IOptions<DocugenOptions> options, IHostEnvironment environment)
    {
        var store = options.Value.Store;
        if (string.IsNullOrWhiteSpace(store.ConnectionString))
        {
            throw new InvalidOperationException("Docugen:Store:ConnectionString is not configured. It points at the database holding the Reports and Exports tables.");
        }

        _provider = store.Provider.Trim();
        IsSqlite = DbConnectionFactory.IsSqlite(_provider);
        _connectionString = IsSqlite
            ? DbConnectionFactory.ResolveSqlitePath(store.ConnectionString, environment.ContentRootPath)
            : store.ConnectionString;

        var prefix = IsSqlite ? "" : "docugen.";
        Reports = prefix + "Reports";
        ReportParameters = prefix + "ReportParameters";
        ReportColumns = prefix + "ReportColumns";
        ReportPdfSettings = prefix + "ReportPdfSettings";
        ReportAssets = prefix + "ReportAssets";
        ReportValidation = prefix + "ReportValidation";
        Exports = prefix + "Exports";
        ExportEvents = prefix + "ExportEvents";
    }

    public bool IsSqlite { get; }

    public string Reports { get; }

    public string ReportParameters { get; }

    public string ReportColumns { get; }

    public string ReportPdfSettings { get; }

    public string ReportAssets { get; }

    public string ReportValidation { get; }

    public string Exports { get; }

    public string ExportEvents { get; }

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = DbConnectionFactory.CreateConnection(_provider, _connectionString, "Docugen:Store");
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
