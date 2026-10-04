using System.Text.Json;
using Docugen.Core.Reports;

namespace Docugen.Api.Endpoints;

/// <param name="Format">pdf, xlsx or csv.</param>
/// <param name="Parameters">Parameter values by name, e.g. <c>{ "FromDate": "2026-01-01" }</c>.</param>
public sealed record RenderRequest(OutputFormat? Format, Dictionary<string, JsonElement>? Parameters);

public sealed record ReportSummary(string Key, string Name, string? Description, IReadOnlyList<OutputFormat> Formats)
{
    public static ReportSummary From(ReportDefinition report) => new(report.Key, report.Name, report.Description, report.Formats);
}

public sealed record ReportDetails(
    string Key,
    string Name,
    string? Description,
    IReadOnlyList<OutputFormat> Formats,
    IReadOnlyList<ReportParameterInfo> Parameters,
    IReadOnlyList<ReportColumnInfo> Columns)
{
    public static ReportDetails From(ReportDefinition report) => new(
        report.Key,
        report.Name,
        report.Description,
        report.Formats,
        report.Parameters.Select(p => new ReportParameterInfo(p.Name, p.Type, p.Required, p.Label ?? p.Name, p.Description, p.Default, p.AllowedValues, p.MaxLength)).ToList(),
        report.Columns.Select(c => new ReportColumnInfo(c.Field, c.Header ?? c.Field)).ToList());
}

public sealed record ReportParameterInfo(
    string Name,
    ParameterType Type,
    bool Required,
    string Label,
    string? Description,
    JsonElement? Default,
    IReadOnlyList<string>? AllowedValues,
    int? MaxLength);

public sealed record ReportColumnInfo(string Field, string Header);
