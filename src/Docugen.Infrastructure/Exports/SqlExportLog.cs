using System.Data.Common;
using Docugen.Core.Exports;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Logging;

namespace Docugen.Infrastructure.Exports;

public sealed class SqlExportLog(StoreDatabase store) : IExportLog
{
    private const int MaxErrorMessageLength = 2000;
    private const int MaxEventMessageLength = 4000;

    private const string Columns = """
        ExportId, ReportKey, Format, Parameters, CallerId, RequestedBy, Status, RowsExported, TotalRows, FileName,
        ContentType, FileSize, ErrorCode, ErrorMessage, StartedAtUtc, CompletedAtUtc, DurationMs
        """;

    public Task CreateAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken) =>
        WriteAsync(record, events, insertOnly: true, cancellationToken);

    public Task SaveAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken) =>
        WriteAsync(record, events, insertOnly: false, cancellationToken);

    public async Task<ExportRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM {store.Exports} WHERE ExportId = @id";
        command.AddParameter("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExportRecord
        {
            Id = reader.GetGuid(0),
            ReportKey = reader.GetString(1),
            Format = Enum.Parse<OutputFormat>(reader.GetString(2), ignoreCase: true),
            Parameters = reader.GetNullableString(3),
            CallerId = reader.GetString(4),
            RequestedBy = reader.GetNullableString(5),
            Status = Enum.Parse<ExportStatus>(reader.GetString(6)),
            RowsExported = reader.GetInt64(7),
            TotalRows = reader.GetNullableInt64(8),
            FileName = reader.GetNullableString(9),
            ContentType = reader.GetNullableString(10),
            FileSize = reader.GetNullableInt64(11),
            ErrorCode = reader.GetNullableString(12),
            ErrorMessage = reader.GetNullableString(13),
            StartedAt = reader.GetNullableUtc(14)!.Value,
            CompletedAt = reader.GetNullableUtc(15),
            DurationMs = reader.GetNullableInt64(16),
        };
    }

    public async Task<IReadOnlyList<ExportEvent>> GetEventsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT OccurredAtUtc, Level, EventType, Message FROM {store.ExportEvents} WHERE ExportId = @id ORDER BY OccurredAtUtc, EventId";
        command.AddParameter("@id", id);

        var events = new List<ExportEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new ExportEvent(id, reader.GetNullableUtc(0)!.Value, Enum.Parse<LogLevel>(reader.GetString(1)), reader.GetString(2), reader.GetString(3)));
        }

        return events;
    }

    private async Task WriteAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, bool insertOnly, CancellationToken cancellationToken)
    {
        await using var connection = await store.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // An update that finds no row means the first write failed; insert instead, so the record is not lost.
        var written = insertOnly ? 0 : await ExecuteAsync(connection, transaction, $"""
            UPDATE {store.Exports}
            SET Status = @status, RowsExported = @rowsExported, TotalRows = @totalRows, FileName = @fileName,
                ContentType = @contentType, FileSize = @fileSize, ErrorCode = @errorCode, ErrorMessage = @errorMessage,
                CompletedAtUtc = @completedAt, DurationMs = @durationMs
            WHERE ExportId = @id
            """, record, cancellationToken).ConfigureAwait(false);

        if (written == 0)
        {
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO {store.Exports} ({Columns})
                VALUES (@id, @reportKey, @format, @parameters, @callerId, @requestedBy, @status, @rowsExported, @totalRows, @fileName,
                        @contentType, @fileSize, @errorCode, @errorMessage, @startedAt, @completedAt, @durationMs)
                """, record, cancellationToken).ConfigureAwait(false);
        }

        foreach (var e in events)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {store.ExportEvents} (ExportId, OccurredAtUtc, Level, EventType, Message)
                VALUES (@exportId, @occurredAt, @level, @eventType, @message)
                """;
            command.AddParameter("@exportId", e.ExportId);
            command.AddParameter("@occurredAt", e.OccurredAt);
            command.AddParameter("@level", e.Level.ToString());
            command.AddParameter("@eventType", e.EventType);
            command.AddParameter("@message", Truncate(e.Message, MaxEventMessageLength));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql, ExportRecord record, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.AddParameter("@id", record.Id);
        command.AddParameter("@reportKey", record.ReportKey);
        command.AddParameter("@format", record.Format.ToString().ToLowerInvariant());
        command.AddParameter("@parameters", record.Parameters);
        command.AddParameter("@callerId", record.CallerId);
        command.AddParameter("@requestedBy", record.RequestedBy);
        command.AddParameter("@status", record.Status.ToString());
        command.AddParameter("@rowsExported", record.RowsExported);
        command.AddParameter("@totalRows", record.TotalRows);
        command.AddParameter("@fileName", record.FileName);
        command.AddParameter("@contentType", record.ContentType);
        command.AddParameter("@fileSize", record.FileSize);
        command.AddParameter("@errorCode", record.ErrorCode);
        command.AddParameter("@errorMessage", Truncate(record.ErrorMessage, MaxErrorMessageLength));
        command.AddParameter("@startedAt", record.StartedAt);
        command.AddParameter("@completedAt", record.CompletedAt);
        command.AddParameter("@durationMs", record.DurationMs);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength ? value[..(maxLength - 1)] + "…" : value;
}
