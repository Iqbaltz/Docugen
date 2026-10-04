using System.Text.Json.Serialization;

namespace Docugen.Core.Reports;

[JsonConverter(typeof(JsonStringEnumConverter<OutputFormat>))]
public enum OutputFormat
{
    [JsonStringEnumMemberName("pdf")]
    Pdf,

    [JsonStringEnumMemberName("xlsx")]
    Xlsx,

    [JsonStringEnumMemberName("csv")]
    Csv,
}
