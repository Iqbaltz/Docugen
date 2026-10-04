using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Docugen.Infrastructure.Data;

/// <summary>Reports unhealthy when any configured data source cannot be reached.</summary>
internal sealed class DataSourcesHealthCheck(IDbConnectionFactory connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failures = new Dictionary<string, object>();
        foreach (var name in connections.DataSourceNames)
        {
            try
            {
                await using var connection = connections.Create(name);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                command.CommandTimeout = 5;
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures[name] = ex.GetType().Name;
            }
        }

        return failures.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("One or more data sources are unreachable.", data: failures);
    }
}
