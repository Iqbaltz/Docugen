using System.Globalization;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;

namespace Docugen.Core.Rendering;

public sealed record RenderContext(
    ReportDefinition Report,
    IReadOnlyList<BoundParameter> Parameters,
    CultureInfo Culture,
    DateTimeOffset GeneratedAt,
    string? GeneratedBy);
