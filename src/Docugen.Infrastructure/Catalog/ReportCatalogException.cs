namespace Docugen.Infrastructure.Catalog;

/// <summary>One or more report definitions are invalid. The application refuses to start until they are fixed.</summary>
public sealed class ReportCatalogException(IReadOnlyList<string> errors)
    : Exception("Invalid report definitions:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
