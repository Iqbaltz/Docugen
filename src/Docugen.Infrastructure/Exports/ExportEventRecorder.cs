using System.Globalization;
using Docugen.Core.Exports;
using Microsoft.Extensions.Logging;

namespace Docugen.Infrastructure.Exports;

/// <summary>
/// Passes generator progress on to the caller's progress and turns stage changes into export log events, which wait
/// here until they are written.
/// </summary>
internal sealed class ExportEventRecorder(Guid exportId, IReportProgress inner, TimeProvider timeProvider) : IReportProgress
{
    private readonly Lock _lock = new();
    private readonly List<ExportEvent> _pending = [];
    private long _rows;

    public long RowsSoFar => Volatile.Read(ref _rows);

    public long? TotalRows { get; private set; }

    public void StageChanged(ExportStage stage)
    {
        inner.StageChanged(stage);
        Add(LogLevel.Information, stage.ToString(), stage switch
        {
            ExportStage.CountingRows => "Running the count query.",
            ExportStage.Querying => "Running the report query.",
            ExportStage.WritingRows => "Writing rows.",
            ExportStage.Finalizing => string.Create(CultureInfo.InvariantCulture, $"Read {RowsSoFar:N0} rows; completing the file."),
            _ => stage.ToString(),
        });
    }

    public void TotalRowsCounted(long totalRows)
    {
        TotalRows = totalRows;
        inner.TotalRowsCounted(totalRows);
        Add(LogLevel.Information, "Counted", string.Create(CultureInfo.InvariantCulture, $"The count query returned {totalRows:N0} rows."));
    }

    public void RowsWritten(long rows)
    {
        Volatile.Write(ref _rows, rows);
        inner.RowsWritten(rows);
    }

    public void Add(LogLevel level, string eventType, string message)
    {
        var e = new ExportEvent(exportId, timeProvider.GetUtcNow(), level, eventType, message);
        lock (_lock)
        {
            _pending.Add(e);
        }
    }

    public List<ExportEvent> Drain()
    {
        lock (_lock)
        {
            var events = _pending.ToList();
            _pending.Clear();
            return events;
        }
    }

    /// <summary>Puts events back after a failed write; they keep their timestamps and are written next time.</summary>
    public void Requeue(IEnumerable<ExportEvent> events)
    {
        lock (_lock)
        {
            _pending.InsertRange(0, events);
        }
    }
}
