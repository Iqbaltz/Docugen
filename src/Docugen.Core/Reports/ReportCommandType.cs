using System.Text.Json.Serialization;

namespace Docugen.Core.Reports;

[JsonConverter(typeof(JsonStringEnumConverter<ReportCommandType>))]
public enum ReportCommandType
{
    /// <summary>The query is a SQL batch that uses <c>@Name</c> placeholders.</summary>
    Text,

    /// <summary>The query is the name of a stored procedure; parameters are passed by name.</summary>
    StoredProcedure,
}
