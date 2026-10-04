using System.Data;
using System.Data.Common;
using System.Text.Json;
using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Data;

/// <summary>Runs report queries with ADO.NET and streams the rows back.</summary>
public sealed class SqlReportDataSource(IDbConnectionFactory connections, IOptions<DocugenOptions> options) : IReportDataSource
{
    private static readonly HashSet<string> DateTypeNames = new(StringComparer.OrdinalIgnoreCase) { "DATE", "DATETIME", "DATETIME2", "TIMESTAMP" };

    public async Task<ReportData> ExecuteAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken)
    {
        var connection = connections.Create(report.DataSource);
        DbCommand? command = null;
        DbDataReader? reader = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            command = connection.CreateCommand();
            command.CommandText = report.Query;
            command.CommandType = report.CommandType == ReportCommandType.StoredProcedure ? CommandType.StoredProcedure : CommandType.Text;
            command.CommandTimeout = report.CommandTimeoutSeconds ?? options.Value.Limits.DefaultCommandTimeoutSeconds;
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(CreateParameter(command, parameter, connection is SqliteConnection));
            }

            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var (columns, ordinals, parseDates) = ResolveColumns(report, reader);
            return new DbReportData(report.Key, columns, ordinals, parseDates, connection, command, reader);
        }
        catch (Exception ex)
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            if (command is not null)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);

            if (IsTimeout(ex))
            {
                throw new ReportQueryTimeoutException(report.Key, ex);
            }

            throw;
        }
    }

    internal static bool IsTimeout(Exception exception) => exception is SqlException { Number: -2 };

    private static DbParameter CreateParameter(DbCommand command, BoundParameter parameter, bool isSqlite)
    {
        var dbParameter = command.CreateParameter();
        dbParameter.ParameterName = "@" + parameter.Name;
        dbParameter.DbType = parameter.Type switch
        {
            ParameterType.Int => DbType.Int32,
            ParameterType.Long => DbType.Int64,
            ParameterType.Decimal => DbType.Decimal,
            ParameterType.Bool => DbType.Boolean,
            ParameterType.Date => DbType.Date,
            ParameterType.DateTime => DbType.DateTime2,
            _ => DbType.String,
        };

        dbParameter.Value = parameter.Value switch
        {
            null => DBNull.Value,
            // Sqlite stores dates as text and compares them as yyyy-MM-dd; SQL Server takes a DateTime.
            DateOnly date => isSqlite ? date : date.ToDateTime(TimeOnly.MinValue),
            int[] numbers => JsonSerializer.Serialize(numbers),
            string[] strings => JsonSerializer.Serialize(strings),
            var value => value,
        };

        if (dbParameter.DbType == DbType.String)
        {
            // A fixed size keeps SQL Server from caching a separate plan for every string length.
            dbParameter.Size = dbParameter.Value is string { Length: > 4000 } ? -1 : 4000;
        }

        return dbParameter;
    }

    private static (ColumnInfo[] Columns, int[] Ordinals, bool[] ParseDates) ResolveColumns(ReportDefinition report, DbDataReader reader)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            byName.TryAdd(reader.GetName(i), i);
        }

        int[] ordinals;
        ColumnInfo[] columns;
        if (report.Columns.Count == 0)
        {
            ordinals = Enumerable.Range(0, reader.FieldCount).ToArray();
            columns = ordinals.Select(i => new ColumnInfo(reader.GetName(i), reader.GetName(i), reader.GetFieldType(i))).ToArray();
        }
        else
        {
            ordinals = new int[report.Columns.Count];
            columns = new ColumnInfo[report.Columns.Count];
            for (var i = 0; i < report.Columns.Count; i++)
            {
                var definition = report.Columns[i];
                if (!byName.TryGetValue(definition.Field, out var ordinal))
                {
                    throw new ReportDefinitionMismatchException(
                        $"Report '{report.Key}' lists column '{definition.Field}' but the query does not return it. " +
                        $"Returned columns: {string.Join(", ", byName.Keys)}.");
                }

                ordinals[i] = ordinal;
                columns[i] = new ColumnInfo(definition.Field, definition.Header ?? definition.Field, reader.GetFieldType(ordinal), definition.Format, definition.Width);
            }
        }

        // Providers without a native date type (Sqlite) return dates as text; parse columns declared as dates.
        var parseDates = ordinals.Select(o => reader.GetFieldType(o) == typeof(string) && DateTypeNames.Contains(SafeDataTypeName(reader, o))).ToArray();
        return (columns, ordinals, parseDates);
    }

    private static string SafeDataTypeName(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetDataTypeName(ordinal);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }
}
