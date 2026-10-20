using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Docugen.Core;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Rendering.Pdf;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Report definitions and template assets read from the store: <c>Reports</c> with its <c>ReportParameters</c>,
/// <c>ReportColumns</c> and <c>ReportPdfSettings</c> rows, and <c>ReportAssets</c>. <see cref="RefreshAsync"/>
/// re-reads them all; only reports whose rows changed are validated again. Invalid reports are left out (the rest
/// keep working), logged, and recorded in <c>ReportValidation</c> so authors can see why.
/// </summary>
public sealed partial class SqlReportCatalog(
    StoreDatabase store,
    IOptions<DocugenOptions> options,
    LiquidTemplateEngine templates,
    TimeProvider timeProvider,
    ILogger<SqlReportCatalog> logger) : IReportCatalog, IReportAssets
{
    private static readonly Dictionary<string, ParameterType> ParameterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["string"] = ParameterType.String,
        ["int"] = ParameterType.Int,
        ["long"] = ParameterType.Long,
        ["decimal"] = ParameterType.Decimal,
        ["bool"] = ParameterType.Bool,
        ["date"] = ParameterType.Date,
        ["datetime"] = ParameterType.DateTime,
        ["string[]"] = ParameterType.StringArray,
        ["int[]"] = ParameterType.IntArray,
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, LoadedReport> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private volatile Snapshot _snapshot = new(new Dictionary<string, ReportDefinition>(StringComparer.OrdinalIgnoreCase), ReportAssets.Empty);
    private string _assetsFingerprint = "";

    public IReadOnlyCollection<ReportDefinition> All => _snapshot.Reports.Values;

    public ReportDefinition? Find(string key) => _snapshot.Reports.GetValueOrDefault(key);

    IReadOnlyDictionary<string, object?> IReportAssets.Values => _snapshot.Assets.Values;

    /// <summary>Re-reads every active report and all assets from the store.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ReportSource> sources;
            List<ReportAsset> assets;
            await using (var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                sources = await ReadReportsAsync(connection, cancellationToken).ConfigureAwait(false);
                assets = await ReadAssetsAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            var changed = new List<LoadedReport>();
            var reports = new Dictionary<string, ReportDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (!_loaded.TryGetValue(source.Report.Key, out var loaded) || !loaded.Source.SameAs(source))
                {
                    loaded = Load(source);
                    _loaded[source.Report.Key] = loaded;
                    changed.Add(loaded);
                    LogResult(loaded);
                }

                if (loaded.Report is not null)
                {
                    reports[loaded.Report.Key] = loaded.Report;
                }
            }

            var present = sources.Select(s => s.Report.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = _loaded.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var key in removed)
            {
                _loaded.Remove(key);
                LogReportRemoved(logger, key);
            }

            _snapshot = new Snapshot(reports, BuildAssets(assets));

            if (changed.Count > 0 || removed.Count > 0)
            {
                LogCatalogChanged(logger, reports.Count, _loaded.Count - reports.Count);
                await SaveValidationAsync(changed, removed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private LoadedReport Load(ReportSource source)
    {
        var errors = new List<string>();
        var report = Parse(source, errors);
        var warnings = new List<string>();
        if (report is not null)
        {
            (var validationErrors, warnings) = ReportValidator.Validate(report, options.Value, templates);
            errors.AddRange(validationErrors);
        }

        return new LoadedReport(source, errors.Count == 0 ? report : null, errors, warnings);
    }

    /// <summary>Turns a report's rows into a definition, or adds errors for values that cannot be read.</summary>
    internal static ReportDefinition? Parse(ReportSource source, List<string> errors)
    {
        var row = source.Report;
        var commandType = (row.CommandType ?? "").Trim().ToUpperInvariant() switch
        {
            "" or "TEXT" => ReportCommandType.Text,
            "STOREDPROCEDURE" => ReportCommandType.StoredProcedure,
            _ => (ReportCommandType?)null,
        };
        if (commandType is null)
        {
            errors.Add($"CommandType '{row.CommandType}' is not supported. Use Text or StoredProcedure.");
        }

        var formats = new List<OutputFormat>();
        foreach (var name in SplitList(row.Formats))
        {
            OutputFormat? format = name.ToUpperInvariant() switch
            {
                "PDF" => OutputFormat.Pdf,
                "XLSX" => OutputFormat.Xlsx,
                "CSV" => OutputFormat.Csv,
                _ => null,
            };
            if (format is { } known)
            {
                formats.Add(known);
            }
            else
            {
                errors.Add($"Formats: '{name}' is not a format. Use pdf, xlsx or csv.");
            }
        }

        var parameters = new List<ParameterDefinition>();
        foreach (var parameter in source.Parameters)
        {
            if (!ParameterTypes.TryGetValue((parameter.Type ?? "string").Trim(), out var type))
            {
                errors.Add($"ReportParameters: '{parameter.Name}' has type '{parameter.Type}'. Use one of: {string.Join(", ", ParameterTypes.Keys)}.");
                continue;
            }

            parameters.Add(new ParameterDefinition
            {
                Name = parameter.Name.Trim(),
                Type = type,
                Required = parameter.IsRequired,
                Label = Blank(parameter.Label),
                Description = Blank(parameter.Description),
                Default = DefaultValue(type, parameter.DefaultValue),
                MaxLength = parameter.MaxLength,
                AllowedValues = parameter.AllowedValues is null ? null : SplitList(parameter.AllowedValues),
            });
        }

        if (errors.Count > 0)
        {
            return null;
        }

        var pdf = source.Pdf;
        return new ReportDefinition
        {
            Key = row.Key,
            Name = row.Name ?? "",
            Description = Blank(row.Description),
            DataSource = row.DataSource ?? "",
            CommandType = commandType!.Value,
            Query = row.Query?.Trim() ?? "",
            CountQuery = Blank(row.CountQuery)?.Trim(),
            CommandTimeoutSeconds = row.CommandTimeoutSeconds,
            Formats = formats,
            Parameters = parameters,
            Columns = source.Columns.Select(c => new ColumnDefinition { Field = c.Field.Trim(), Header = Blank(c.Header), Format = Blank(c.Format), Width = c.Width }).ToList(),
            FileName = Blank(row.FileName)?.Trim(),
            Culture = Blank(pdf?.Culture)?.Trim(),
            PdfTemplateSource = Blank(pdf?.Template),
            Pdf = pdf is null
                ? new PdfOptions()
                : new PdfOptions
                {
                    PageSize = Blank(pdf.PageSize)?.Trim() ?? "A4",
                    Landscape = pdf.Landscape,
                    Margin = Blank(pdf.Margin)?.Trim() ?? "15mm",
                    MaxRows = pdf.MaxRows,
                    ShowPageNumbers = pdf.ShowPageNumbers,
                },
        };
    }

    /// <summary>
    /// A default stored as text becomes the JSON value a caller would send: a list for array types, a number or
    /// true/false where the text is one, otherwise a string (which the binder still converts, or reports as invalid).
    /// </summary>
    private static JsonElement? DefaultValue(ParameterType type, string? text)
    {
        if (text is null)
        {
            return null;
        }

        var json = type switch
        {
            ParameterType.StringArray or ParameterType.IntArray => JsonSerializer.Serialize(SplitList(text)),
            ParameterType.Int or ParameterType.Long or ParameterType.Decimal
                when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number.ToString(CultureInfo.InvariantCulture),
            ParameterType.Bool when bool.TryParse(text, out var flag) => flag ? "true" : "false",
            _ => JsonSerializer.Serialize(text),
        };

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string> SplitList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private ReportAssets BuildAssets(List<ReportAsset> assets)
    {
        var fingerprint = string.Join("|", assets.Select(a => $"{a.FileName}:{a.Text?.Length ?? a.Data?.Length ?? 0}:{a.Text?.GetHashCode(StringComparison.Ordinal) ?? 0}"));
        if (fingerprint != _assetsFingerprint)
        {
            _assetsFingerprint = fingerprint;
            foreach (var asset in assets.Where(a => !ReportAssets.IsSupported(a.FileName)))
            {
                LogUnsupportedAsset(logger, asset.FileName);
            }

            LogAssetsLoaded(logger, assets.Count);
        }

        return new ReportAssets(assets);
    }

    private async Task<List<ReportSource>> ReadReportsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var reports = await QueryAsync(connection, $"""
            SELECT ReportKey, Name, Description, DataSource, CommandType, Query, CountQuery, CommandTimeoutSeconds, Formats, FileName
            FROM {store.Reports}
            WHERE IsActive = 1
            """, r => new ReportRow(
                r.GetString(0),
                r.GetNullableString(1),
                r.GetNullableString(2),
                r.GetNullableString(3),
                r.GetNullableString(4),
                r.GetNullableString(5),
                r.GetNullableString(6),
                r.GetNullableInt32(7),
                r.GetNullableString(8),
                r.GetNullableString(9)), cancellationToken).ConfigureAwait(false);

        var parameters = await QueryAsync(connection, $"""
            SELECT ReportKey, Name, SortOrder, Type, IsRequired, Label, Description, DefaultValue, MaxLength, AllowedValues
            FROM {store.ReportParameters}
            ORDER BY ReportKey, SortOrder, Name
            """, r => new ParameterRow(
                r.GetString(0),
                r.GetString(1),
                r.GetInt32(2),
                r.GetNullableString(3),
                !r.IsDBNull(4) && r.GetBoolean(4),
                r.GetNullableString(5),
                r.GetNullableString(6),
                r.GetNullableString(7),
                r.GetNullableInt32(8),
                r.GetNullableString(9)), cancellationToken).ConfigureAwait(false);

        var columns = await QueryAsync(connection, $"""
            SELECT ReportKey, Field, SortOrder, Header, Format, Width
            FROM {store.ReportColumns}
            ORDER BY ReportKey, SortOrder, Field
            """, r => new ColumnRow(
                r.GetString(0),
                r.GetString(1),
                r.GetInt32(2),
                r.GetNullableString(3),
                r.GetNullableString(4),
                r.IsDBNull(5) ? null : Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)), cancellationToken).ConfigureAwait(false);

        var pdfSettings = await QueryAsync(connection, $"""
            SELECT ReportKey, Template, PageSize, Landscape, Margin, MaxRows, ShowPageNumbers, Culture
            FROM {store.ReportPdfSettings}
            """, r => new PdfRow(
                r.GetString(0),
                r.GetNullableString(1),
                r.GetNullableString(2),
                !r.IsDBNull(3) && r.GetBoolean(3),
                r.GetNullableString(4),
                r.GetNullableInt32(5),
                r.IsDBNull(6) || r.GetBoolean(6),
                r.GetNullableString(7)), cancellationToken).ConfigureAwait(false);

        var parametersByReport = parameters.ToLookup(p => p.ReportKey, StringComparer.OrdinalIgnoreCase);
        var columnsByReport = columns.ToLookup(c => c.ReportKey, StringComparer.OrdinalIgnoreCase);
        var pdfByReport = pdfSettings.ToDictionary(p => p.ReportKey, StringComparer.OrdinalIgnoreCase);
        return reports
            .Select(r => new ReportSource(r, parametersByReport[r.Key].ToList(), columnsByReport[r.Key].ToList(), pdfByReport.GetValueOrDefault(r.Key)))
            .ToList();
    }

    private static async Task<List<T>> QueryAsync<T>(DbConnection connection, string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private async Task<List<ReportAsset>> ReadAssetsAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await QueryAsync(
            connection,
            $"SELECT FileName, TextContent, BinaryContent FROM {store.ReportAssets}",
            r => new ReportAsset(r.GetString(0), r.GetNullableString(1), r.IsDBNull(2) ? null : r.GetFieldValue<byte[]>(2)),
            cancellationToken).ConfigureAwait(false);

    /// <summary>Records the outcome for changed reports so authors can query <c>ReportValidation</c>.</summary>
    private async Task SaveValidationAsync(List<LoadedReport> changed, List<string> removed, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var checkedAt = timeProvider.GetUtcNow();

            foreach (var key in removed)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {store.ReportValidation} WHERE ReportKey = @key";
                delete.AddParameter("@key", key);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var loaded in changed)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = $"""
                    DELETE FROM {store.ReportValidation} WHERE ReportKey = @key;
                    INSERT INTO {store.ReportValidation} (ReportKey, IsValid, Errors, Warnings, CheckedAtUtc)
                    VALUES (@key, @valid, @errors, @warnings, @checkedAt);
                    """;
                upsert.AddParameter("@key", loaded.Source.Report.Key);
                upsert.AddParameter("@valid", loaded.Report is not null);
                upsert.AddParameter("@errors", loaded.Errors.Count == 0 ? null : string.Join(Environment.NewLine, loaded.Errors));
                upsert.AddParameter("@warnings", loaded.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, loaded.Warnings));
                upsert.AddParameter("@checkedAt", checkedAt);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSaveValidationFailed(logger, ex);
        }
    }

    private void LogResult(LoadedReport loaded)
    {
        var key = loaded.Source.Report.Key;
        if (loaded.Report is null)
        {
            LogReportInvalid(logger, key, string.Join(" | ", loaded.Errors));
            return;
        }

        LogReportLoaded(logger, key);
        foreach (var warning in loaded.Warnings)
        {
            LogReportWarning(logger, key, warning);
        }
    }

    /// <summary>A <c>Reports</c> row as stored.</summary>
    internal sealed record ReportRow(
        string Key,
        string? Name,
        string? Description,
        string? DataSource,
        string? CommandType,
        string? Query,
        string? CountQuery,
        int? CommandTimeoutSeconds,
        string? Formats,
        string? FileName);

    /// <summary>A <c>ReportParameters</c> row as stored.</summary>
    internal sealed record ParameterRow(
        string ReportKey,
        string Name,
        int SortOrder,
        string? Type,
        bool IsRequired,
        string? Label,
        string? Description,
        string? DefaultValue,
        int? MaxLength,
        string? AllowedValues);

    /// <summary>A <c>ReportColumns</c> row as stored.</summary>
    internal sealed record ColumnRow(string ReportKey, string Field, int SortOrder, string? Header, string? Format, double? Width);

    /// <summary>A <c>ReportPdfSettings</c> row as stored.</summary>
    internal sealed record PdfRow(string ReportKey, string? Template, string? PageSize, bool Landscape, string? Margin, int? MaxRows, bool ShowPageNumbers, string? Culture);

    /// <summary>Everything stored for one report. <see cref="SameAs"/> detects changes between refreshes.</summary>
    internal sealed record ReportSource(ReportRow Report, IReadOnlyList<ParameterRow> Parameters, IReadOnlyList<ColumnRow> Columns, PdfRow? Pdf)
    {
        public bool SameAs(ReportSource other) =>
            Report == other.Report && Parameters.SequenceEqual(other.Parameters) && Columns.SequenceEqual(other.Columns) && Pdf == other.Pdf;
    }

    private sealed record LoadedReport(ReportSource Source, ReportDefinition? Report, List<string> Errors, List<string> Warnings);

    private sealed record Snapshot(Dictionary<string, ReportDefinition> Reports, ReportAssets Assets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Report {ReportKey} loaded")]
    private static partial void LogReportLoaded(ILogger logger, string reportKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Report {ReportKey} is invalid and cannot be run: {Errors}")]
    private static partial void LogReportInvalid(ILogger logger, string reportKey, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Report {ReportKey}: {Warning}")]
    private static partial void LogReportWarning(ILogger logger, string reportKey, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Report {ReportKey} was removed or deactivated")]
    private static partial void LogReportRemoved(ILogger logger, string reportKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Report catalog updated: {ValidCount} reports available, {InvalidCount} invalid")]
    private static partial void LogCatalogChanged(ILogger logger, int validCount, int invalidCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded {Count} template assets")]
    private static partial void LogAssetsLoaded(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Template asset {FileName} is ignored: only images, fonts, .css, .html and .txt files are supported")]
    private static partial void LogUnsupportedAsset(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save report validation results")]
    private static partial void LogSaveValidationFailed(ILogger logger, Exception exception);
}
