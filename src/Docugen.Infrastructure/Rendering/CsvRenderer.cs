using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Rendering;

/// <summary>Streams rows to RFC 4180 CSV.</summary>
public sealed class CsvRenderer(IOptions<DocugenOptions> options) : IDocumentRenderer
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    public OutputFormat Format => OutputFormat.Csv;

    public string ContentType => "text/csv";

    public string FileExtension => ".csv";

    public long? GetRowLimit(ReportDefinition report) => null;

    public async Task RenderAsync(RenderContext context, ReportData data, Stream output, CancellationToken cancellationToken)
    {
        var settings = options.Value.Csv;
        var culture = string.IsNullOrEmpty(settings.Culture) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(settings.Culture);
        var configuration = new CsvConfiguration(culture)
        {
            Delimiter = settings.Delimiter,
            NewLine = "\r\n",
            InjectionOptions = InjectionOptions.None, // applied below to text values only, so negative numbers stay intact
        };

        var writer = new StreamWriter(output, new UTF8Encoding(settings.IncludeBom), bufferSize: 65536, leaveOpen: true);
        await using var csv = new CsvWriter(writer, configuration);

        foreach (var column in data.Columns)
        {
            csv.WriteField(column.Header);
        }

        await csv.NextRecordAsync().ConfigureAwait(false);

        await foreach (var row in data.ReadRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var value in row)
            {
                csv.WriteField(FormatValue(value, culture, settings.EscapeFormulas));
            }

            await csv.NextRecordAsync().ConfigureAwait(false);
        }

        await csv.FlushAsync().ConfigureAwait(false);
    }

    internal static string? FormatValue(object? value, CultureInfo culture, bool escapeFormulas) => value switch
    {
        null => null,
        string text => escapeFormulas ? EscapeFormula(text) : text,
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero
            ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFF", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.FFFzzz", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFF", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        byte[] bytes => Convert.ToBase64String(bytes),
        IFormattable formattable => formattable.ToString(null, culture),
        _ => escapeFormulas ? EscapeFormula(value.ToString() ?? "") : value.ToString(),
    };

    /// <summary>Stops spreadsheet apps from running text that looks like a formula (OWASP CSV injection guidance).</summary>
    internal static string EscapeFormula(string text) =>
        text.Length > 0 && FormulaPrefixes.Contains(text[0]) ? "'" + text : text;
}
