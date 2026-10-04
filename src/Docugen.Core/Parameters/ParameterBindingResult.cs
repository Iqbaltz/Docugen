namespace Docugen.Core.Parameters;

public sealed class ParameterBindingResult
{
    internal ParameterBindingResult(IReadOnlyList<BoundParameter> parameters, IReadOnlyDictionary<string, string[]> errors)
    {
        Parameters = parameters;
        Errors = errors;
    }

    public IReadOnlyList<BoundParameter> Parameters { get; }

    /// <summary>Validation errors keyed by parameter name, in the shape ASP.NET Core ValidationProblem expects.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}
