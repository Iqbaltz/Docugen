using System.Runtime.CompilerServices;

namespace Docugen.Core.Data;

/// <summary>Report data backed by rows already in memory; handy for previews and tests.</summary>
public sealed class InMemoryReportData(IReadOnlyList<ColumnInfo> columns, IEnumerable<object?[]> rows) : ReportData(columns)
{
    protected override async IAsyncEnumerable<object?[]> ReadRowsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
