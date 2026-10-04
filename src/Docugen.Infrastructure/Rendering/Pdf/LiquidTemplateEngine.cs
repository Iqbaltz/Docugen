using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using Fluid;
using Fluid.Values;

namespace Docugen.Infrastructure.Rendering.Pdf;

/// <summary>
/// Compiles and renders Liquid templates with Fluid. Output is HTML-encoded by default, so data values can never
/// inject markup; templates opt out per value with the <c>raw</c> filter. The model is plain dictionaries and lists,
/// so templates cannot reach arbitrary .NET members.
/// </summary>
public sealed class LiquidTemplateEngine
{
    private static readonly FluidParser Parser = new();

    private readonly TemplateOptions _options = new();
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new();

    public LiquidTemplateEngine()
    {
        _options.ValueConverters.Add(value => value is DateOnly date ? new DateTimeValue(date.ToDateTime(TimeOnly.MinValue)) : null);
    }

    public bool TryCompile(string source, out string? error)
    {
        var compiled = Parser.TryParse(source, out _, out error);
        return compiled;
    }

    /// <summary>Returns the compiled template, compiling and caching it on first use.</summary>
    public IFluidTemplate GetTemplate(string cacheKey, string source) =>
        _cache.GetOrAdd(cacheKey, static (_, text) =>
            Parser.TryParse(text, out var template, out var error)
                ? template
                : throw new InvalidOperationException($"Template does not compile: {error}"), source);

    public ValueTask<string> RenderAsync(IFluidTemplate template, IReadOnlyDictionary<string, object?> model, CultureInfo culture)
    {
        var context = new TemplateContext(_options) { CultureInfo = culture };
        foreach (var (name, value) in model)
        {
            context.SetValue(name, value);
        }

        return template.RenderAsync(context, HtmlEncoder.Default);
    }
}
