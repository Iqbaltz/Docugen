using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Docugen.Infrastructure.Store;

/// <summary>Reports unhealthy when Docugen's own database cannot be reached.</summary>
internal sealed class StoreHealthCheck(StoreDatabase store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {store.Reports}";
            command.CommandTimeout = 5;
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The Docugen store is unreachable.", data: new Dictionary<string, object> { ["error"] = ex.GetType().Name });
        }
    }
}
