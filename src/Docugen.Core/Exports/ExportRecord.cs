using Docugen.Core.Reports;

namespace Docugen.Core.Exports;

/// <summary>One export call, as recorded in the export log: what was asked for, by whom, and how it ended.</summary>
public sealed record ExportRecord
{
    public required Guid Id { get; init; }

    public required string ReportKey { get; init; }

    public required OutputFormat Format { get; init; }

    /// <summary>Bound parameter values as a JSON object.</summary>
    public string? Parameters { get; init; }

    /// <summary>Stable id of the caller (the token subject).</summary>
    public required string CallerId { get; init; }

    /// <summary>Readable caller name: the user, or the client application for app-only tokens.</summary>
    public string? RequestedBy { get; init; }

    public ExportStatus Status { get; init; } = ExportStatus.Running;

    public long RowsExported { get; init; }

    /// <summary>Rows the count query reported, when the report has one.</summary>
    public long? TotalRows { get; init; }

    /// <summary>Download name, including the extension.</summary>
    public string? FileName { get; init; }

    public string? ContentType { get; init; }

    public long? FileSize { get; init; }

    /// <summary>Machine-readable failure reason, e.g. <c>TOO_MANY_ROWS</c>. See <see cref="ExportErrorCodes"/>.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public long? DurationMs { get; init; }
}

public enum ExportStatus
{
    Running,

    /// <summary>The whole file was sent to the caller.</summary>
    Succeeded,
    Failed,

    /// <summary>The caller cancelled the call, its deadline passed, or it disconnected.</summary>
    Cancelled,
}

public enum ExportStage
{
    /// <summary>Waiting for a free export slot (<c>Limits:MaxConcurrentExports</c>).</summary>
    Queued,
    CountingRows,
    Querying,
    WritingRows,

    /// <summary>All rows are read; the file is being completed (for PDFs, this is when it is printed).</summary>
    Finalizing,
    Completed,
}

public static class ExportErrorCodes
{
    public const string TooManyRows = "TOO_MANY_ROWS";
    public const string QueryTimeout = "QUERY_TIMEOUT";
    public const string DefinitionMismatch = "DEFINITION_MISMATCH";
    public const string Interrupted = "INTERRUPTED";
    public const string Internal = "INTERNAL";
}
