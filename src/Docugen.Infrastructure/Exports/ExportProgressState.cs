using Docugen.Core.Exports;

namespace Docugen.Infrastructure.Exports;

/// <summary>An export's progress at one moment. Percent runs from 0 to 100.</summary>
public readonly record struct ExportProgressSnapshot(ExportStage Stage, int Percent, long RowsWritten, long? TotalRows);

/// <summary>
/// Collects progress from the generator so a reader (the gRPC call) can send it at its own pace. Recording a row is a
/// single field write, so it costs next to nothing per row; a stage change also wakes a waiting reader at once.
/// </summary>
public sealed class ExportProgressState : IReportProgress
{
    private readonly SemaphoreSlim _stageChanged = new(0);
    private int _stage = (int)ExportStage.Queued;
    private long _rows;
    private long _totalRows = -1;
    private int _stopped;

    /// <summary>True once <see cref="Stop"/> was called, e.g. because the export failed.</summary>
    public bool IsStopped => Volatile.Read(ref _stopped) == 1;

    public void StageChanged(ExportStage stage)
    {
        Volatile.Write(ref _stage, (int)stage);
        _stageChanged.Release();
    }

    public void TotalRowsCounted(long totalRows) => Volatile.Write(ref _totalRows, totalRows);

    public void RowsWritten(long rows) => Volatile.Write(ref _rows, rows);

    /// <summary>Ends the progress without completing it; readers stop waiting.</summary>
    public void Stop()
    {
        Volatile.Write(ref _stopped, 1);
        _stageChanged.Release();
    }

    public ExportProgressSnapshot Read()
    {
        var stage = (ExportStage)Volatile.Read(ref _stage);
        var rows = Volatile.Read(ref _rows);
        var total = Volatile.Read(ref _totalRows);
        long? totalRows = total < 0 ? null : total;
        return new ExportProgressSnapshot(stage, Percent(stage, rows, totalRows), rows, totalRows);
    }

    /// <summary>Waits until the stage changes, the progress is stopped, or <paramref name="timeout"/> passes.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _stageChanged.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// 0 while queued, 2 while counting, 5 while querying, 10 to 90 while rows are written (in proportion to the count
    /// query's total; without one it stays at 10), 90 while the file is completed, 100 when it is ready.
    /// </summary>
    public static int Percent(ExportStage stage, long rows, long? totalRows) => stage switch
    {
        ExportStage.Queued => 0,
        ExportStage.CountingRows => 2,
        ExportStage.Querying => 5,
        ExportStage.WritingRows when totalRows is > 0 => 10 + (int)(80 * Math.Min(rows, totalRows.Value) / totalRows.Value),
        ExportStage.WritingRows => 10,
        ExportStage.Finalizing => 90,
        _ => 100,
    };
}
