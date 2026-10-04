using Docugen.Core.Reports;

namespace Docugen.Core.Parameters;

/// <summary>A validated parameter value converted to its declared CLR type (null when not supplied).</summary>
public sealed record BoundParameter(string Name, ParameterType Type, object? Value);
