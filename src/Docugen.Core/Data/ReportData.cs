using System.Runtime.CompilerServices;

namespace Docugen.Core.Data;

/// <summary>
/// The result of a report query: its columns and a forward-only stream of rows.
/// Rows can be read once. Each row holds one value per column, with database nulls as <c>null</c>.
/// </summary>
public abstract class ReportData : IAsyncDisposable
{
    private bool _read;

    protected ReportData(IReadOnlyList<ColumnInfo> columns)
    {
        Columns = columns;
    }

    public IReadOnlyList<ColumnInfo> Columns { get; }

    /// <summary>Rows read so far.</summary>
    public long RowCount { get; private set; }

    /// <summary>Maximum rows allowed; reading past it throws <see cref="ReportLimitExceededException"/>.</summary>
    public long? RowLimit { get; set; }

    public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_read)
        {
            throw new InvalidOperationException("Report rows can only be read once.");
        }

        _read = true;
        await foreach (var row in ReadRowsCoreAsync(cancellationToken).ConfigureAwait(false))
        {
            if (RowLimit is { } limit && RowCount >= limit)
            {
                throw new ReportLimitExceededException(limit);
            }

            RowCount++;
            yield return row;
        }
    }

    protected abstract IAsyncEnumerable<object?[]> ReadRowsCoreAsync(CancellationToken cancellationToken);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
