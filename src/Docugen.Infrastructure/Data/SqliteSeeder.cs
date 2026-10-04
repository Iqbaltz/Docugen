using Docugen.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Data;

/// <summary>Creates Sqlite demo databases from their seed script when the database file does not exist yet.</summary>
internal sealed partial class SqliteSeeder(
    IOptions<DocugenOptions> options,
    IHostEnvironment environment,
    ILogger<SqliteSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, dataSource) in options.Value.DataSources)
        {
            if (!DbConnectionFactory.IsSqlite(dataSource.Provider) || string.IsNullOrWhiteSpace(dataSource.SeedScript))
            {
                continue;
            }

            var connectionString = DbConnectionFactory.ResolveSqlitePath(dataSource.ConnectionString, environment.ContentRootPath);
            var path = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (File.Exists(path))
            {
                continue;
            }

            var script = await File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, dataSource.SeedScript), cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            LogSeeded(logger, name, path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Sqlite data source {Name} at {Path} from its seed script")]
    private static partial void LogSeeded(ILogger logger, string name, string path);
}
