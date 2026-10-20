using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Docugen.Core;
using Docugen.Core.Exports;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Exports;

/// <summary>The caller of an export: a stable id (the token subject) and a readable name for logs and the PDF footer.</summary>
public sealed record ExportCaller(string Id, string Name);

/// <summary>A generated file, positioned at the start, ready to send. It is deleted when the export finishes.</summary>
public sealed record ExportedFile(Guid ExportId, string FileName, string ContentType, long Size, long RowCount, Stream Content);

/// <summary>
/// Runs one export for a caller: waits for a free slot, generates the file into a temporary file while reporting
/// progress, then hands it to the caller's <c>deliver</c> callback to send. Rendering to a file first means a failure
/// part-way through is reported as an error rather than a truncated file. Every export is recorded in the export log.
/// </summary>
public sealed partial class ReportExporter : IDisposable
{
    private readonly ReportGenerator _generator;
    private readonly IExportLog _log;
    private readonly DocugenOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ILogger _audit;
    private readonly ConcurrencyLimiter _slots;

    public ReportExporter(ReportGenerator generator, IExportLog log, IOptions<DocugenOptions> options, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _generator = generator;
        _log = log;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<ReportExporter>();
        _audit = loggerFactory.CreateLogger("Docugen.Audit");
        _slots = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = _options.Limits.MaxConcurrentExports,
            QueueLimit = _options.Limits.MaxQueuedExports,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>Runs the export. Parameters must already be bound and valid.</summary>
    /// <param name="progress">Receives the stage and row count; <see cref="ExportStage.Completed"/> just before <paramref name="deliver"/>.</param>
    /// <param name="deliver">Sends the finished file. The export only counts as succeeded once it returns.</param>
    /// <exception cref="ExportBusyException">Too many exports are running and waiting.</exception>
    /// <exception cref="ExportFailedException">The export failed; its code says why.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<ExportRecord> ExportAsync(
        ReportDefinition report,
        OutputFormat format,
        IReadOnlyList<BoundParameter> parameters,
        ExportCaller caller,
        IReportProgress progress,
        Func<ExportedFile, CancellationToken, Task> deliver,
        CancellationToken cancellationToken)
    {
        using var slot = await _slots.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!slot.IsAcquired)
        {
            LogBusy(_logger, caller.Name, report.Key);
            throw new ExportBusyException(_options.Limits.MaxConcurrentExports, _options.Limits.MaxQueuedExports);
        }

        var startedAt = _timeProvider.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        var parametersJson = JsonSerializer.Serialize(parameters.ToDictionary(p => p.Name, p => p.Value));
        var record = new ExportRecord
        {
            Id = Guid.CreateVersion7(startedAt),
            ReportKey = report.Key,
            Format = format,
            Parameters = parametersJson,
            CallerId = caller.Id,
            RequestedBy = caller.Name,
            StartedAt = startedAt,
        };

        var events = new ExportEventRecorder(record.Id, progress, _timeProvider);
        events.Add(LogLevel.Information, "Started", $"{caller.Name} requested {report.Key} as {Name(format)} with parameters {parametersJson}.");
        await WriteLogAsync(pending => _log.CreateAsync(record, pending, CancellationToken.None), events, record.Id).ConfigureAwait(false);
        LogStarted(_logger, record.Id, caller.Name, report.Key, format);

        await using var file = CreateTempFile(_options.TempPath);
        try
        {
            var result = await _generator.GenerateAsync(report, format, parameters, caller.Name, file, events, cancellationToken).ConfigureAwait(false);
            file.Position = 0;
            record = record with { RowsExported = result.RowCount, FileName = result.FileName, ContentType = result.ContentType, FileSize = file.Length };
            events.Add(LogLevel.Information, "Generated", Invariant($"{result.RowCount:N0} rows, {file.Length:N0} bytes in {Stopwatch.GetElapsedTime(started).TotalSeconds:N1} s."));

            progress.StageChanged(ExportStage.Completed);
            await deliver(new ExportedFile(record.Id, result.FileName, result.ContentType, file.Length, result.RowCount, file), cancellationToken).ConfigureAwait(false);

            record = Finish(record, ExportStatus.Succeeded, events, started);
            events.Add(LogLevel.Information, "Sent", Invariant($"Sent {file.Length:N0} bytes to {caller.Name}."));
            LogAudit(_audit, caller.Name, report.Key, format, result.RowCount, record.Id, parametersJson);
            return record;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            record = Finish(record, ExportStatus.Cancelled, events, started) with
            {
                ErrorMessage = "The call ended early: the caller cancelled it, its deadline passed, it disconnected or the service is stopping.",
            };
            events.Add(LogLevel.Warning, "Cancelled", record.ErrorMessage!);
            LogCancelled(_logger, record.Id);
            throw new OperationCanceledException(record.ErrorMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            var (code, message) = Describe(ex, format, record.Id);
            record = Finish(record, ExportStatus.Failed, events, started) with { ErrorCode = code, ErrorMessage = message };
            events.Add(LogLevel.Error, "Failed", $"{code}: {ex.GetType().Name}: {ex.Message}");
            LogFailed(_logger, code == ExportErrorCodes.Internal ? ex : null, record.Id, code);
            throw new ExportFailedException(code, message, record.Id, ex);
        }
        finally
        {
            await WriteLogAsync(pending => _log.SaveAsync(record, pending, CancellationToken.None), events, record.Id).ConfigureAwait(false);
        }
    }

    public void Dispose() => _slots.Dispose();

    private ExportRecord Finish(ExportRecord record, ExportStatus status, ExportEventRecorder events, long started) => record with
    {
        Status = status,
        RowsExported = Math.Max(record.RowsExported, events.RowsSoFar),
        TotalRows = events.TotalRows,
        CompletedAt = _timeProvider.GetUtcNow(),
        DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
    };

    /// <summary>The error code and the message shown to the caller. Full details go to the export log only.</summary>
    private static (string Code, string Message) Describe(Exception exception, OutputFormat format, Guid exportId) => exception switch
    {
        ReportLimitExceededException limit => (
            ExportErrorCodes.TooManyRows,
            format == OutputFormat.Pdf ? $"{limit.Message} Narrow the filters or export as xlsx or csv." : $"{limit.Message} Narrow the filters."),
        ReportQueryTimeoutException => (ExportErrorCodes.QueryTimeout, "The report query took too long. Narrow the filters or try again later."),
        ReportDefinitionMismatchException mismatch => (ExportErrorCodes.DefinitionMismatch, mismatch.Message),
        _ => (ExportErrorCodes.Internal, $"Something went wrong while producing the file. Quote export {exportId} to the Docugen administrators."),
    };

    /// <summary>Writes pending events (and the record) to the log. A failure is logged and never fails the export.</summary>
    private async Task WriteLogAsync(Func<IReadOnlyList<ExportEvent>, Task> write, ExportEventRecorder events, Guid exportId)
    {
        var pending = events.Drain();
        try
        {
            await write(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            events.Requeue(pending);
            LogWriteFailed(_logger, ex, exportId);
        }
    }

    private static FileStream CreateTempFile(string? tempPath)
    {
        var folder = string.IsNullOrWhiteSpace(tempPath) ? Path.GetTempPath() : tempPath;
        Directory.CreateDirectory(folder);

        // Read access too: the XLSX writer reads back parts of the zip package while writing it.
        return new FileStream(
            Path.Combine(folder, $"docugen-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    private static string Name(OutputFormat format) => format.ToString().ToLowerInvariant();

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Export {ExportId} started: {User} requested {ReportKey} as {Format}")]
    private static partial void LogStarted(ILogger logger, Guid exportId, string user, string reportKey, OutputFormat format);

    [LoggerMessage(Level = LogLevel.Information, Message = "AUDIT {User} exported {ReportKey} as {Format}: {RowCount} rows, export {ExportId}, parameters {Parameters}")]
    private static partial void LogAudit(ILogger logger, string user, string reportKey, OutputFormat format, long rowCount, Guid exportId, string parameters);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Docugen is busy; rejected {ReportKey} for {User}")]
    private static partial void LogBusy(ILogger logger, string user, string reportKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Export {ExportId} cancelled")]
    private static partial void LogCancelled(ILogger logger, Guid exportId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export {ExportId} failed: {ErrorCode}")]
    private static partial void LogFailed(ILogger logger, Exception? exception, Guid exportId, string errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write export {ExportId} to the export log; retrying with the next write")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, Guid exportId);
}
