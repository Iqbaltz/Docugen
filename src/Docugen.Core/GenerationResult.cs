namespace Docugen.Core;

/// <summary>Describes a document written by <see cref="ReportGenerator"/>.</summary>
/// <param name="FileName">Download name, including the extension.</param>
public sealed record GenerationResult(string ContentType, string FileName, long RowCount);
