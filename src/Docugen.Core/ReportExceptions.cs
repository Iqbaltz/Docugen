namespace Docugen.Core;

/// <summary>The query returned more rows than the requested output format allows.</summary>
public sealed class ReportLimitExceededException(long limit)
    : Exception($"The report returned more than {limit:N0} rows, which is the limit for this output format.")
{
    public long Limit { get; } = limit;
}

/// <summary>The report query did not finish within its command timeout.</summary>
public sealed class ReportQueryTimeoutException(string reportKey, Exception innerException)
    : Exception($"The query for report '{reportKey}' timed out.", innerException)
{
    public string ReportKey { get; } = reportKey;
}

/// <summary>The query result does not match the report definition (for example a missing column).</summary>
public sealed class ReportDefinitionMismatchException(string message) : Exception(message);
