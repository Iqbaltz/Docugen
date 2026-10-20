using System.Diagnostics;
using System.Globalization;
using Docugen.Core.Data;
using Docugen.Core.Exports;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Core;

/// <summary>Runs a report query and renders the result into a document of the requested format.</summary>
public sealed partial class ReportGenerator(
    IReportDataSource dataSource,
    IEnumerable<IDocumentRenderer> renderers,
    IOptions<DocugenOptions> options,
    TimeProvider timeProvider,
    ILogger<ReportGenerator> logger)
{
    private readonly Dictionary<OutputFormat, IDocumentRenderer> _renderers = renderers.ToDictionary(r => r.Format);

    public IDocumentRenderer GetRenderer(OutputFormat format) =>
        _renderers.TryGetValue(format, out var renderer)
            ? renderer
            : throw new NotSupportedException($"No renderer is registered for {format}.");

    /// <summary>
    /// Generates the document into <paramref name="output"/>. Parameters must already be bound with
    /// <see cref="ParameterBinder"/>. If the report has a count query it runs first, so an export over the row
    /// limit fails before the main query starts.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(
        ReportDefinition report,
        OutputFormat format,
        IReadOnlyList<BoundParameter> parameters,
        string? generatedBy,
        Stream output,
        IReportProgress? progress,
        CancellationToken cancellationToken)
    {
        if (!report.Supports(format))
        {
            throw new NotSupportedException($"Report '{report.Key}' does not support {format}.");
        }

        var renderer = GetRenderer(format);
        var settings = options.Value;
        var started = Stopwatch.GetTimestamp();
        var now = timeProvider.GetLocalNow();
        var context = new RenderContext(report, parameters, ResolveCulture(report.Culture ?? settings.DefaultCulture), now, generatedBy);
        var rowLimit = renderer.GetRowLimit(report) is { } formatLimit
            ? Math.Min(formatLimit, settings.Limits.MaxRows)
            : settings.Limits.MaxRows;

        if (!string.IsNullOrWhiteSpace(report.CountQuery))
        {
            progress?.StageChanged(ExportStage.CountingRows);
            var total = await dataSource.CountAsync(report, parameters, cancellationToken).ConfigureAwait(false);
            progress?.TotalRowsCounted(total);
            if (total > rowLimit)
            {
                throw new ReportLimitExceededException(rowLimit);
            }
        }

        progress?.StageChanged(ExportStage.Querying);
        long rowCount;
        await using (var data = await dataSource.ExecuteAsync(report, parameters, cancellationToken).ConfigureAwait(false))
        {
            data.RowLimit = rowLimit;
            if (progress is not null)
            {
                progress.StageChanged(ExportStage.WritingRows);
                data.RowRead = progress.RowsWritten;
                data.ReadCompleted = () => progress.StageChanged(ExportStage.Finalizing);
            }

            await renderer.RenderAsync(context, data, output, cancellationToken).ConfigureAwait(false);
            rowCount = data.RowCount;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        var fileName = FileNameFormatter.Format(report.FileName, report.Key, parameters, now, renderer.FileExtension);
        LogGenerated(logger, report.Key, format, generatedBy ?? "(anonymous)", rowCount, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new GenerationResult(renderer.ContentType, fileName, rowCount);
    }

    private static CultureInfo ResolveCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated report {ReportKey} as {Format} for {User}: {RowCount} rows in {ElapsedMs} ms")]
    private static partial void LogGenerated(ILogger logger, string reportKey, OutputFormat format, string user, long rowCount, long elapsedMs);
}
