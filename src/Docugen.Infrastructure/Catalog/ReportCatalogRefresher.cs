using Docugen.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Loads the report catalog before the server starts (so an unreachable store stops startup), then re-reads it
/// every <c>Docugen:Catalog:RefreshInterval</c>. A failed refresh keeps the previous catalog.
/// </summary>
internal sealed partial class ReportCatalogRefresher(
    SqlReportCatalog catalog,
    IOptions<DocugenOptions> options,
    TimeProvider timeProvider,
    ILogger<ReportCatalogRefresher> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Catalog.RefreshInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await catalog.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRefreshFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh the report catalog; the previous definitions stay in use")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);
}
