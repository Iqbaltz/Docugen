using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docugen.Core.Reports;

public sealed record ParameterDefinition
{
    public required string Name { get; init; }

    public ParameterType Type { get; init; } = ParameterType.String;

    public bool Required { get; init; }

    public string? Label { get; init; }

    public string? Description { get; init; }

    /// <summary>Value used when the caller omits the parameter.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>Maximum length for string values.</summary>
    public int? MaxLength { get; init; }

    /// <summary>When set, string values must be one of these (case-insensitive).</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ParameterType>))]
public enum ParameterType
{
    [JsonStringEnumMemberName("string")]
    String,

    [JsonStringEnumMemberName("int")]
    Int,

    [JsonStringEnumMemberName("long")]
    Long,

    [JsonStringEnumMemberName("decimal")]
    Decimal,

    [JsonStringEnumMemberName("bool")]
    Bool,

    [JsonStringEnumMemberName("date")]
    Date,

    [JsonStringEnumMemberName("datetime")]
    DateTime,

    /// <summary>Sent to the database as a JSON array string; read it with <c>OPENJSON(@Name)</c>.</summary>
    [JsonStringEnumMemberName("string[]")]
    StringArray,

    /// <summary>Sent to the database as a JSON array string; read it with <c>OPENJSON(@Name)</c>.</summary>
    [JsonStringEnumMemberName("int[]")]
    IntArray,
}
