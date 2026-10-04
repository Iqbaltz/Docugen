using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Docugen.Core;
using Docugen.Core.Data;

namespace Docugen.Infrastructure.Data;

/// <summary>Streams rows from an open data reader and owns the connection, command and reader.</summary>
internal sealed class DbReportData(
    string reportKey,
    IReadOnlyList<ColumnInfo> columns,
    int[] ordinals,
    bool[] parseDates,
    DbConnection connection,
    DbCommand command,
    DbDataReader reader) : ReportData(columns)
{
    protected override async IAsyncEnumerable<object?[]> ReadRowsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var raw = new object[reader.FieldCount];
        while (await ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            reader.GetValues(raw);
            var row = new object?[ordinals.Length];
            for (var i = 0; i < ordinals.Length; i++)
            {
                var value = raw[ordinals[i]];
                row[i] = value switch
                {
                    DBNull => null,
                    string text when parseDates[i] && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
                    _ => value,
                };
            }

            yield return row;
        }
    }

    private async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (SqlReportDataSource.IsTimeout(ex))
        {
            throw new ReportQueryTimeoutException(reportKey, ex);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await reader.DisposeAsync().ConfigureAwait(false);
        await command.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
