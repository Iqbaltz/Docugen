using System.Collections;
using System.Globalization;
using System.Net;
using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Rendering.Pdf;

/// <summary>Renders the report's Liquid template (or the built-in table template) to HTML, then prints it with Chromium.</summary>
public sealed class PdfRenderer(
    LiquidTemplateEngine templates,
    ChromiumPdfConverter converter,
    IReportAssets assets,
    IOptions<DocugenOptions> options) : IDocumentRenderer
{
    private static readonly Lazy<string> DefaultTemplate = new(LoadDefaultTemplate);

    public OutputFormat Format => OutputFormat.Pdf;

    public string ContentType => "application/pdf";

    public string FileExtension => ".pdf";

    public long? GetRowLimit(ReportDefinition report) => report.Pdf.MaxRows ?? options.Value.Limits.MaxPdfRows;

    public async Task RenderAsync(RenderContext context, ReportData data, Stream output, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>();
        await foreach (var row in data.ReadRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        var html = await RenderHtmlAsync(context, data.Columns, rows).ConfigureAwait(false);
        var report = context.Report;
        var footer = report.Pdf.ShowPageNumbers ? BuildFooter(report.Name) : null;
        var pdf = await converter.ConvertAsync(html, new PdfPageSettings(report.Pdf.PageSize, report.Pdf.Landscape, report.Pdf.Margin, footer), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(pdf, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders the template to HTML without printing it; useful for previews and template development.</summary>
    public async Task<string> RenderHtmlAsync(RenderContext context, IReadOnlyList<ColumnInfo> columns, IReadOnlyList<object?[]> rows)
    {
        var report = context.Report;
        var template = report.PdfTemplateSource is { } source
            ? templates.GetTemplate($"{report.Key}:{source.GetHashCode(StringComparison.Ordinal)}", source)
            : templates.GetTemplate("__default", DefaultTemplate.Value);

        var model = BuildModel(context, columns, rows, assets.Values);
        return await templates.RenderAsync(template, model, context.Culture).ConfigureAwait(false);
    }

    internal static Dictionary<string, object?> BuildModel(
        RenderContext context,
        IReadOnlyList<ColumnInfo> columns,
        IReadOnlyList<object?[]> rows,
        IReadOnlyDictionary<string, object?> assets)
    {
        var culture = context.Culture;
        var kinds = columns.Select((column, i) => ColumnKind(column, rows, i)).ToArray();

        var columnModels = columns.Select((column, i) => (object?)new Dictionary<string, object?>
        {
            ["index"] = i,
            ["field"] = column.Field,
            ["header"] = column.Header,
            ["kind"] = kinds[i],
            ["numeric"] = kinds[i] is "integer" or "number",
            ["format"] = column.Format,
        }).ToList();

        var rowModels = new List<object?>(rows.Count);
        var displayRows = new List<object?>(rows.Count);
        foreach (var row in rows)
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var display = new List<object?>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                values[columns[i].Field] = ToTemplateValue(row[i]);
                display.Add(FormatForDisplay(row[i], kinds[i], culture));
            }

            rowModels.Add(values);
            displayRows.Add(display);
        }

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var parameterList = new List<object?>();
        foreach (var parameter in context.Parameters)
        {
            parameters[parameter.Name] = ToTemplateValue(parameter.Value);
            var definition = context.Report.Parameters.FirstOrDefault(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            parameterList.Add(new Dictionary<string, object?>
            {
                ["name"] = parameter.Name,
                ["label"] = definition?.Label ?? parameter.Name,
                ["display"] = FormatParameter(parameter, culture),
            });
        }

        return new Dictionary<string, object?>
        {
            ["report"] = new Dictionary<string, object?>
            {
                ["key"] = context.Report.Key,
                ["name"] = context.Report.Name,
                ["description"] = context.Report.Description,
            },
            ["parameters"] = parameters,
            ["parameter_list"] = parameterList,
            ["columns"] = columnModels,
            ["rows"] = rowModels,
            ["display_rows"] = displayRows,
            ["row_count"] = rows.Count,
            ["generated_at"] = context.GeneratedAt,
            ["generated_at_display"] = context.GeneratedAt.ToString("g", culture),
            ["generated_by"] = context.GeneratedBy,
            ["assets"] = assets.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string ColumnKind(ColumnInfo column, IReadOnlyList<object?[]> rows, int index)
    {
        var sample = rows.Select(r => r[index]).FirstOrDefault(v => v is not null);
        return (sample?.GetType() ?? column.DataType) switch
        {
            var t when t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) => "integer",
            var t when t == typeof(decimal) || t == typeof(double) || t == typeof(float) => "number",
            var t when t == typeof(DateTime) => sample is DateTime { TimeOfDay.Ticks: > 0 } ? "datetime" : "date",
            var t when t == typeof(DateOnly) => "date",
            var t when t == typeof(DateTimeOffset) => "datetime",
            var t when t == typeof(bool) => "bool",
            _ => "text",
        };
    }

    private static object? ToTemplateValue(object? value) => value switch
    {
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        TimeOnly or TimeSpan or Guid => Convert.ToString(value, CultureInfo.InvariantCulture),
        byte[] => "[binary]",
        string[] strings => strings.Cast<object?>().ToList(),
        int[] numbers => numbers.Cast<object?>().ToList(),
        _ => value,
    };

    private static string FormatForDisplay(object? value, string kind, CultureInfo culture) => value switch
    {
        null => "",
        DateTime dateTime => dateTime.ToString(kind == "datetime" || dateTime.TimeOfDay.Ticks > 0 ? "g" : "d", culture),
        DateOnly date => date.ToString("d", culture),
        DateTimeOffset offset => offset.ToString("g", culture),
        bool flag => flag ? "Yes" : "No",
        decimal or double or float => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("N2", culture),
        byte or sbyte or short or ushort or int or uint or long or ulong => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("N0", culture),
        byte[] => "[binary]",
        IFormattable formattable => formattable.ToString(null, culture),
        _ => value.ToString() ?? "",
    };

    private static string FormatParameter(BoundParameter parameter, CultureInfo culture) => parameter.Value switch
    {
        null => "All",
        DateOnly date => date.ToString("d", culture),
        DateTime dateTime => dateTime.ToString("g", culture),
        bool flag => flag ? "Yes" : "No",
        string text => text,
        IEnumerable items => string.Join(", ", items.Cast<object>()),
        IFormattable formattable => formattable.ToString(null, culture),
        var value => value.ToString() ?? "",
    };

    private static string BuildFooter(string reportName) =>
        "<div style=\"width:100%;font-size:7pt;color:#6b7280;font-family:Arial,sans-serif;padding:0 12mm;display:flex;justify-content:space-between;\">" +
        $"<span>{WebUtility.HtmlEncode(reportName)}</span>" +
        "<span>Page <span class=\"pageNumber\"></span> of <span class=\"totalPages\"></span></span></div>";

    private static string LoadDefaultTemplate()
    {
        using var stream = typeof(PdfRenderer).Assembly.GetManifestResourceStream("Docugen.Templates.default.html")
            ?? throw new InvalidOperationException("The built-in PDF template is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
