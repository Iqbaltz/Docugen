using System.Data;
using System.Data.Common;

namespace Docugen.Infrastructure.Store;

internal static class DbCommandExtensions
{
    /// <summary>Adds a parameter, storing nulls as <c>DBNull</c> and times as UTC <c>datetime2</c>.</summary>
    public static void AddParameter(this DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        switch (value)
        {
            case null:
                parameter.Value = DBNull.Value;
                break;
            case DateTimeOffset time:
                parameter.DbType = DbType.DateTime2;
                parameter.Value = time.UtcDateTime;
                break;
            default:
                parameter.Value = value;
                break;
        }

        command.Parameters.Add(parameter);
    }

    public static string? GetNullableString(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static int? GetNullableInt32(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static long? GetNullableInt64(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>Reads a UTC <c>datetime2</c> column (text on Sqlite).</summary>
    public static DateTimeOffset? GetNullableUtc(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
}
