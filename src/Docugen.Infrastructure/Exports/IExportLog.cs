using Docugen.Core.Exports;
using Microsoft.Extensions.Logging;

namespace Docugen.Infrastructure.Exports;

/// <summary>One line of an export's history in <c>ExportEvents</c>.</summary>
public sealed record ExportEvent(Guid ExportId, DateTimeOffset OccurredAt, LogLevel Level, string EventType, string Message);

/// <summary>Records every export call (<c>Exports</c>) and what happened during it (<c>ExportEvents</c>).</summary>
public interface IExportLog
{
    /// <summary>Records a new export and its first events.</summary>
    Task CreateAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken);

    /// <summary>Saves the export's current state and appends events, in one transaction. Inserts the export if it is missing.</summary>
    Task SaveAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken);

    Task<ExportRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExportEvent>> GetEventsAsync(Guid id, CancellationToken cancellationToken);
}
