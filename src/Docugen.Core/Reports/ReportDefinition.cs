using System.Text.Json.Serialization;

namespace Docugen.Core.Reports;

/// <summary>
/// A named, reviewed report: the SQL it runs, the parameters callers may supply,
/// who may run it and how each output format is produced.
/// </summary>
public sealed record ReportDefinition
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Name of a data source configured under <c>Docugen:DataSources</c>.</summary>
    public required string DataSource { get; init; }

    public ReportCommandType CommandType { get; init; } = ReportCommandType.Text;

    /// <summary>SQL text or stored procedure name. Loaded from <c>query.sql</c> when not given inline.</summary>
    public string Query { get; init; } = "";

    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Roles allowed to run the report. Empty means every authenticated user.</summary>
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];

    public IReadOnlyList<OutputFormat> Formats { get; init; } = [OutputFormat.Pdf, OutputFormat.Xlsx, OutputFormat.Csv];

    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = [];

    /// <summary>Output columns. When empty, every column returned by the query is used as-is.</summary>
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    /// <summary>
    /// Download file name without extension. Supports <c>{ParameterName}</c>, <c>{ParameterName:format}</c>
    /// and <c>{Now:format}</c> tokens.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>Culture used for number and date formatting in PDF output, e.g. <c>id-ID</c>.</summary>
    public string? Culture { get; init; }

    public PdfOptions Pdf { get; init; } = new();

    /// <summary>Contents of the PDF template file, loaded by the catalog. Null means the built-in template.</summary>
    [JsonIgnore]
    public string? PdfTemplateSource { get; init; }

    public bool Supports(OutputFormat format) => Formats.Contains(format);
}
