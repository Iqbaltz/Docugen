using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Docugen.Core;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Rendering.Pdf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Loads report definitions from <c>{ReportsPath}/{key}/report.json</c> (plus <c>query.sql</c> and an optional
/// PDF template) and validates all of them up front. Folders starting with <c>_</c> or <c>.</c> are skipped.
/// </summary>
public sealed partial class FileReportCatalog : IReportCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> PageSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Letter", "Legal", "Tabloid", "Ledger", "A0", "A1", "A2", "A3", "A4", "A5", "A6",
    };

    private readonly Dictionary<string, ReportDefinition> _reports;

    public FileReportCatalog(
        IOptions<DocugenOptions> options,
        IHostEnvironment environment,
        LiquidTemplateEngine templates,
        ILogger<FileReportCatalog> logger)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.ReportsPath));
        _reports = Load(root, options.Value, templates, logger);
        LogLoaded(logger, _reports.Count, root);
    }

    public IReadOnlyCollection<ReportDefinition> All => _reports.Values;

    public ReportDefinition? Find(string key) => _reports.GetValueOrDefault(key);

    internal static Dictionary<string, ReportDefinition> Load(string root, DocugenOptions options, LiquidTemplateEngine templates, ILogger logger)
    {
        if (!Directory.Exists(root))
        {
            throw new ReportCatalogException([$"Reports folder '{root}' does not exist."]);
        }

        var errors = new List<string>();
        var reports = new Dictionary<string, ReportDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var folderName = Path.GetFileName(folder);
            if (folderName.StartsWith('_') || folderName.StartsWith('.'))
            {
                continue;
            }

            var reportErrors = new List<string>();
            var report = LoadReport(folder, reportErrors);
            if (report is not null)
            {
                Validate(report, folderName, options, templates, reportErrors, logger);
            }

            errors.AddRange(reportErrors.Select(e => $"{folderName}: {e}"));
            if (report is not null && reportErrors.Count == 0)
            {
                reports[report.Key] = report;
            }
        }

        if (errors.Count > 0)
        {
            throw new ReportCatalogException(errors);
        }

        return reports;
    }

    private static ReportDefinition? LoadReport(string folder, List<string> errors)
    {
        var manifestPath = Path.Combine(folder, "report.json");
        if (!File.Exists(manifestPath))
        {
            errors.Add("report.json is missing.");
            return null;
        }

        ReportDefinition? report;
        try
        {
            report = JsonSerializer.Deserialize<ReportDefinition>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"report.json is invalid: {ex.Message}");
            return null;
        }

        if (report is null)
        {
            errors.Add("report.json is empty.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(report.Query))
        {
            var queryPath = Path.Combine(folder, "query.sql");
            if (File.Exists(queryPath))
            {
                report = report with { Query = File.ReadAllText(queryPath).Trim() };
            }
        }

        if (report.Pdf.Template is { } templateName)
        {
            var templatePath = Path.GetFullPath(Path.Combine(folder, templateName));
            if (!templatePath.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                errors.Add($"pdf.template '{templateName}' must be inside the report folder.");
            }
            else if (!File.Exists(templatePath))
            {
                errors.Add($"pdf.template '{templateName}' does not exist.");
            }
            else
            {
                report = report with { PdfTemplateSource = File.ReadAllText(templatePath) };
            }
        }

        return report;
    }

    private static void Validate(
        ReportDefinition report,
        string folderName,
        DocugenOptions options,
        LiquidTemplateEngine templates,
        List<string> errors,
        ILogger logger)
    {
        if (!KeyRegex().IsMatch(report.Key))
        {
            errors.Add($"key '{report.Key}' must be lower-case letters, digits and dashes.");
        }
        else if (!report.Key.Equals(folderName, StringComparison.Ordinal))
        {
            errors.Add($"key '{report.Key}' must match its folder name.");
        }

        if (string.IsNullOrWhiteSpace(report.Name))
        {
            errors.Add("name is required.");
        }

        if (options.FindDataSource(report.DataSource) is null)
        {
            errors.Add($"dataSource '{report.DataSource}' is not configured under Docugen:DataSources.");
        }

        if (report.Formats.Count == 0)
        {
            errors.Add("formats must list at least one format.");
        }

        if (report.Formats.Distinct().Count() != report.Formats.Count)
        {
            errors.Add("formats contains duplicates.");
        }

        if (report.CommandTimeoutSeconds is <= 0)
        {
            errors.Add("commandTimeoutSeconds must be positive.");
        }

        if (report.AllowedRoles.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("allowedRoles must not contain empty values.");
        }

        if (report.Culture is { } culture && !IsValidCulture(culture))
        {
            errors.Add($"culture '{culture}' is not a known culture.");
        }

        ValidateParameters(report, errors);
        ValidateColumns(report, errors);
        ValidateQuery(report, errors, logger);
        ValidatePdf(report, templates, errors);
    }

    private static void ValidateParameters(ReportDefinition report, List<string> errors)
    {
        foreach (var parameter in report.Parameters)
        {
            if (!IdentifierRegex().IsMatch(parameter.Name))
            {
                errors.Add($"parameter name '{parameter.Name}' must be a valid identifier.");
            }
        }

        foreach (var duplicate in report.Parameters.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"parameter '{duplicate.Key}' is declared more than once.");
        }

        // Binding with no values surfaces defaults that do not match their declared type.
        var binding = ParameterBinder.Bind(report, null);
        foreach (var parameter in report.Parameters.Where(p => p.Default is not null))
        {
            if (binding.Errors.TryGetValue(parameter.Name, out var messages))
            {
                errors.Add($"default for parameter '{parameter.Name}' is invalid: {string.Join(" ", messages)}");
            }
        }
    }

    private static void ValidateColumns(ReportDefinition report, List<string> errors)
    {
        if (report.Columns.Any(c => string.IsNullOrWhiteSpace(c.Field)))
        {
            errors.Add("every column needs a field.");
        }

        foreach (var duplicate in report.Columns.GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"column '{duplicate.Key}' is listed more than once.");
        }

        if (report.Columns.Any(c => c.Width is <= 0 or > 255))
        {
            errors.Add("column widths must be between 0 and 255.");
        }
    }

    private static void ValidateQuery(ReportDefinition report, List<string> errors, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(report.Query))
        {
            errors.Add("no query: add query.sql or set \"query\" in report.json.");
            return;
        }

        if (report.CommandType != ReportCommandType.Text)
        {
            return;
        }

        var referenced = SqlParameterScanner.FindParameters(report.Query);
        var declared = report.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in referenced.Where(n => !declared.Contains(n)).Order(StringComparer.Ordinal))
        {
            errors.Add($"query uses @{name} but no parameter with that name is declared.");
        }

        foreach (var name in declared.Where(n => !referenced.Contains(n)))
        {
            LogUnusedParameter(logger, report.Key, name);
        }
    }

    private static void ValidatePdf(ReportDefinition report, LiquidTemplateEngine templates, List<string> errors)
    {
        if (!PageSizes.Contains(report.Pdf.PageSize))
        {
            errors.Add($"pdf.pageSize '{report.Pdf.PageSize}' is not supported. Use one of: {string.Join(", ", PageSizes)}.");
        }

        if (report.Pdf.MaxRows is <= 0)
        {
            errors.Add("pdf.maxRows must be positive.");
        }

        if (report.PdfTemplateSource is { } source && !templates.TryCompile(source, out var error))
        {
            errors.Add($"pdf template does not compile: {error}");
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} report definitions from {Root}")]
    private static partial void LogLoaded(ILogger logger, int count, string root);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Report {ReportKey} declares parameter {Name} but its query never uses it")]
    private static partial void LogUnusedParameter(ILogger logger, string reportKey, string name);
}
