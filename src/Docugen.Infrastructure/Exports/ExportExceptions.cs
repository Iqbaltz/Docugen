namespace Docugen.Infrastructure.Exports;

/// <summary>Too many exports are running and waiting for a slot.</summary>
public sealed class ExportBusyException(int running, int waiting)
    : Exception($"Docugen is busy: {running} exports are running and {waiting} are waiting. Try again shortly.");

/// <summary>An export failed. <see cref="Code"/> is one of <see cref="Core.Exports.ExportErrorCodes"/>; the message is safe to show callers.</summary>
public sealed class ExportFailedException(string code, string message, Guid exportId, Exception innerException)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    /// <summary>The export's id in the export log, for support.</summary>
    public Guid ExportId { get; } = exportId;
}
