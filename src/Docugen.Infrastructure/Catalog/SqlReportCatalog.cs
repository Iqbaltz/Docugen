using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docugen.Core;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Rendering.Pdf;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Report definitions and template assets read from the store's <c>Reports</c> and <c>ReportAssets</c> tables.
/// <see cref="RefreshAsync"/> re-reads both; only rows that changed are validated again. Invalid rows are left out
/// (the rest keep working), logged, and recorded in <c>ReportValidation</c> so authors can see why.
/// </summary>
public sealed partial class SqlReportCatalog(
    StoreDatabase store,
    IOptions<DocugenOptions> options,
    LiquidTemplateEngine templates,
    TimeProvider timeProvider,
    ILogger<SqlReportCatalog> logger) : IReportCatalog, IReportAssets
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
            List<ReportRow> rows;
            List<ReportAsset> assets;
            await using (var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                rows = await ReadReportsAsync(connection, cancellationToken).ConfigureAwait(false);
                assets = await ReadAssetsAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            var changed = new List<LoadedReport>();
            var reports = new Dictionary<string, ReportDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!_loaded.TryGetValue(row.Key, out var loaded) || loaded.Row != row)
                {
                    loaded = Load(row);
                    _loaded[row.Key] = loaded;
                    changed.Add(loaded);
                    LogResult(loaded);
                }

                if (loaded.Report is not null)
                {
                    reports[loaded.Report.Key] = loaded.Report;
                }
            }

            var present = rows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

    private LoadedReport Load(ReportRow row)
    {
        var errors = new List<string>();
        var report = Parse(row, errors);
        var warnings = new List<string>();
        if (report is not null)
        {
            (var validationErrors, warnings) = ReportValidator.Validate(report, options.Value, templates);
            errors.AddRange(validationErrors);
        }

        return new LoadedReport(row, errors.Count == 0 ? report : null, errors, warnings);
    }

    /// <summary>Turns a <c>Reports</c> row into a definition, or adds errors for columns that cannot be read.</summary>
    internal static ReportDefinition? Parse(ReportRow row, List<string> errors)
    {
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

        var parameters = ParseJson<ParameterDefinition>("Parameters", row.Parameters, errors);
        var columns = ParseJson<ColumnDefinition>("Columns", row.Columns, errors);
        if (errors.Count > 0)
        {
            return null;
        }

        return new ReportDefinition
        {
            Key = row.Key,
            Name = row.Name ?? "",
            Description = row.Description,
            DataSource = row.DataSource ?? "",
            CommandType = commandType!.Value,
            Query = row.Query?.Trim() ?? "",
            CountQuery = string.IsNullOrWhiteSpace(row.CountQuery) ? null : row.CountQuery.Trim(),
            CommandTimeoutSeconds = row.CommandTimeoutSeconds,
            AllowedRoles = SplitList(row.AllowedRoles),
            Formats = formats,
            Parameters = parameters,
            Columns = columns,
            FileName = string.IsNullOrWhiteSpace(row.FileName) ? null : row.FileName.Trim(),
            Culture = string.IsNullOrWhiteSpace(row.Culture) ? null : row.Culture.Trim(),
            PdfTemplateSource = string.IsNullOrWhiteSpace(row.PdfTemplate) ? null : row.PdfTemplate,
            Pdf = new PdfOptions
            {
                PageSize = string.IsNullOrWhiteSpace(row.PdfPageSize) ? "A4" : row.PdfPageSize.Trim(),
                Landscape = row.PdfLandscape,
                Margin = string.IsNullOrWhiteSpace(row.PdfMargin) ? "15mm" : row.PdfMargin.Trim(),
                MaxRows = row.PdfMaxRows,
                ShowPageNumbers = row.PdfShowPageNumbers,
            },
        };
    }

    private static List<T> ParseJson<T>(string column, string? json, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            errors.Add($"{column} is not a valid JSON array: {ex.Message}");
            return [];
        }
    }

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

    private async Task<List<ReportRow>> ReadReportsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ReportKey, Name, Description, DataSource, CommandType, Query, CountQuery, CommandTimeoutSeconds,
                   AllowedRoles, Formats, Parameters, Columns, FileName, Culture,
                   PdfTemplate, PdfPageSize, PdfLandscape, PdfMargin, PdfMaxRows, PdfShowPageNumbers
            FROM {store.Reports}
            WHERE IsActive = 1
            """;

        var rows = new List<ReportRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ReportRow(
                reader.GetString(0),
                reader.GetNullableString(1),
                reader.GetNullableString(2),
                reader.GetNullableString(3),
                reader.GetNullableString(4),
                reader.GetNullableString(5),
                reader.GetNullableString(6),
                reader.GetNullableInt32(7),
                reader.GetNullableString(8),
                reader.GetNullableString(9),
                reader.GetNullableString(10),
                reader.GetNullableString(11),
                reader.GetNullableString(12),
                reader.GetNullableString(13),
                reader.GetNullableString(14),
                reader.GetNullableString(15),
                !reader.IsDBNull(16) && reader.GetBoolean(16),
                reader.GetNullableString(17),
                reader.GetNullableInt32(18),
                reader.IsDBNull(19) || reader.GetBoolean(19)));
        }

        return rows;
    }

    private async Task<List<ReportAsset>> ReadAssetsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT FileName, TextContent, BinaryContent FROM {store.ReportAssets}";

        var assets = new List<ReportAsset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            assets.Add(new ReportAsset(
                reader.GetString(0),
                reader.GetNullableString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2)));
        }

        return assets;
    }

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
                upsert.AddParameter("@key", loaded.Row.Key);
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
        if (loaded.Report is null)
        {
            LogReportInvalid(logger, loaded.Row.Key, string.Join(" | ", loaded.Errors));
            return;
        }

        LogReportLoaded(logger, loaded.Row.Key);
        foreach (var warning in loaded.Warnings)
        {
            LogReportWarning(logger, loaded.Row.Key, warning);
        }
    }

    /// <summary>The raw values of a <c>Reports</c> row. Record equality detects changes between refreshes.</summary>
    internal sealed record ReportRow(
        string Key,
        string? Name,
        string? Description,
        string? DataSource,
        string? CommandType,
        string? Query,
        string? CountQuery,
        int? CommandTimeoutSeconds,
        string? AllowedRoles,
        string? Formats,
        string? Parameters,
        string? Columns,
        string? FileName,
        string? Culture,
        string? PdfTemplate,
        string? PdfPageSize,
        bool PdfLandscape,
        string? PdfMargin,
        int? PdfMaxRows,
        bool PdfShowPageNumbers);

    private sealed record LoadedReport(ReportRow Row, ReportDefinition? Report, List<string> Errors, List<string> Warnings);

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
