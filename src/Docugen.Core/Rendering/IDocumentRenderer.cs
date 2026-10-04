using Docugen.Core.Data;
using Docugen.Core.Reports;

namespace Docugen.Core.Rendering;

public interface IDocumentRenderer
{
    OutputFormat Format { get; }

    string ContentType { get; }

    /// <summary>File extension including the leading dot.</summary>
    string FileExtension { get; }

    /// <summary>Most rows this renderer accepts for the report, or null for no format-specific limit.</summary>
    long? GetRowLimit(ReportDefinition report);

    Task RenderAsync(RenderContext context, ReportData data, Stream output, CancellationToken cancellationToken);
}
