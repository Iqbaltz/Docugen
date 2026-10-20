namespace Docugen.Core.Exports;

/// <summary>Receives progress from <see cref="ReportGenerator"/> while a document is produced.</summary>
public interface IReportProgress
{
    void StageChanged(ExportStage stage);

    void TotalRowsCounted(long totalRows);

    /// <summary>Called after every row with the running total; implementations should be cheap.</summary>
    void RowsWritten(long rows);
}
