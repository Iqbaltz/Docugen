using System.Data.Common;
using Docugen.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Data;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly Dictionary<string, (string Provider, string ConnectionString)> _dataSources = new(StringComparer.OrdinalIgnoreCase);

    public DbConnectionFactory(IOptions<DocugenOptions> options, IHostEnvironment environment)
    {
        foreach (var (name, dataSource) in options.Value.DataSources)
        {
            var provider = dataSource.Provider.Trim();
            var connectionString = IsSqlite(provider)
                ? ResolveSqlitePath(dataSource.ConnectionString, environment.ContentRootPath)
                : dataSource.ConnectionString;
            _dataSources[name] = (provider, connectionString);
        }
    }

    public IReadOnlyCollection<string> DataSourceNames => _dataSources.Keys;

    public DbConnection Create(string dataSource)
    {
        if (!_dataSources.TryGetValue(dataSource, out var config))
        {
            throw new InvalidOperationException($"Data source '{dataSource}' is not configured.");
        }

        if (IsSqlite(config.Provider))
        {
            return new SqliteConnection(config.ConnectionString);
        }

        if (config.Provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlConnection(config.ConnectionString);
        }

        throw new InvalidOperationException($"Data source '{dataSource}' uses unknown provider '{config.Provider}'. Use SqlServer or Sqlite.");
    }

    internal static bool IsSqlite(string provider) => provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes a relative Sqlite file path relative to the content root instead of the working directory.</summary>
    internal static string ResolveSqlitePath(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var path = builder.DataSource;
        if (!string.IsNullOrEmpty(path)
            && path != ":memory:"
            && !path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(path))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRoot, path));
        }

        return builder.ToString();
    }
}
