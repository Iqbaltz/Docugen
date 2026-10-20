using System.Globalization;
using System.Text.RegularExpressions;
using Docugen.Core;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Rendering.Pdf;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Checks a report definition before it can be run: the data source exists, every <c>@Param</c> in the SQL is
/// declared, defaults match their types and the PDF template compiles. Messages name the <c>Reports</c> columns.
/// </summary>
internal static partial class ReportValidator
{
    private static readonly HashSet<string> PageSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Letter", "Legal", "Tabloid", "Ledger", "A0", "A1", "A2", "A3", "A4", "A5", "A6",
    };

    /// <returns>Errors that stop the report from being offered, and warnings that are only logged.</returns>
    public static (List<string> Errors, List<string> Warnings) Validate(ReportDefinition report, DocugenOptions options, LiquidTemplateEngine templates)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!KeyRegex().IsMatch(report.Key))
        {
            errors.Add($"ReportKey '{report.Key}' must be lower-case letters, digits and dashes (at most 64).");
        }

        if (string.IsNullOrWhiteSpace(report.Name))
        {
            errors.Add("Name is required.");
        }

        if (options.FindDataSource(report.DataSource) is null)
        {
            errors.Add($"DataSource '{report.DataSource}' is not configured under Docugen:DataSources.");
        }

        if (report.Formats.Count == 0)
        {
            errors.Add("Formats must list at least one of pdf, xlsx, csv.");
        }

        if (report.Formats.Distinct().Count() != report.Formats.Count)
        {
            errors.Add("Formats contains duplicates.");
        }

        if (report.CommandTimeoutSeconds is <= 0)
        {
            errors.Add("CommandTimeoutSeconds must be positive.");
        }

        if (report.Culture is { } culture && !IsValidCulture(culture))
        {
            errors.Add($"Culture '{culture}' is not a known culture.");
        }

        ValidateParameters(report, errors);
        ValidateColumns(report, errors);
        ValidateQuery(report, errors, warnings);
        ValidatePdf(report, templates, errors);
        return (errors, warnings);
    }

    private static void ValidateParameters(ReportDefinition report, List<string> errors)
    {
        foreach (var parameter in report.Parameters)
        {
            if (!IdentifierRegex().IsMatch(parameter.Name ?? ""))
            {
                errors.Add($"Parameters: name '{parameter.Name}' must be a valid identifier.");
            }
        }

        foreach (var duplicate in report.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Parameters: '{duplicate.Key}' is declared more than once.");
        }

        // Binding with no values surfaces defaults that do not match their declared type.
        var binding = ParameterBinder.Bind(report, null);
        foreach (var parameter in report.Parameters.Where(p => p.Default is not null))
        {
            if (binding.Errors.TryGetValue(parameter.Name, out var messages))
            {
                errors.Add($"Parameters: default for '{parameter.Name}' is invalid: {string.Join(" ", messages)}");
            }
        }
    }

    private static void ValidateColumns(ReportDefinition report, List<string> errors)
    {
        if (report.Columns.Any(c => string.IsNullOrWhiteSpace(c.Field)))
        {
            errors.Add("Columns: every column needs a field.");
        }

        foreach (var duplicate in report.Columns.GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Columns: '{duplicate.Key}' is listed more than once.");
        }

        if (report.Columns.Any(c => c.Width is <= 0 or > 255))
        {
            errors.Add("Columns: widths must be between 0 and 255.");
        }
    }

    private static void ValidateQuery(ReportDefinition report, List<string> errors, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(report.Query))
        {
            errors.Add("Query is empty.");
            return;
        }

        var declared = report.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (report.CommandType == ReportCommandType.Text)
        {
            referenced.UnionWith(CheckDeclared("Query", report.Query, declared, errors));
        }

        if (!string.IsNullOrWhiteSpace(report.CountQuery))
        {
            CheckDeclared("CountQuery", report.CountQuery, declared, errors);
        }

        if (report.CommandType == ReportCommandType.Text)
        {
            foreach (var name in declared.Where(n => !referenced.Contains(n)).Order(StringComparer.Ordinal))
            {
                warnings.Add($"Parameter '{name}' is declared but Query never uses it.");
            }
        }
    }

    private static IReadOnlySet<string> CheckDeclared(string column, string sql, HashSet<string> declared, List<string> errors)
    {
        var referenced = SqlParameterScanner.FindParameters(sql);
        foreach (var name in referenced.Where(n => !declared.Contains(n)).Order(StringComparer.Ordinal))
        {
            errors.Add($"{column} uses @{name} but Parameters does not declare it.");
        }

        return referenced;
    }

    private static void ValidatePdf(ReportDefinition report, LiquidTemplateEngine templates, List<string> errors)
    {
        if (!PageSizes.Contains(report.Pdf.PageSize))
        {
            errors.Add($"PdfPageSize '{report.Pdf.PageSize}' is not supported. Use one of: {string.Join(", ", PageSizes)}.");
        }

        if (report.Pdf.MaxRows is <= 0)
        {
            errors.Add("PdfMaxRows must be positive.");
        }

        if (report.PdfTemplateSource is { } source && !templates.TryCompile(source, out var error))
        {
            errors.Add($"PdfTemplate does not compile: {error}");
        }
    }

    private static bool IsValidCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name, predefinedOnly: true) is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex IdentifierRegex();
}
