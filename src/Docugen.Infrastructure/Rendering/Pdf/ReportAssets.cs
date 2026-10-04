using Docugen.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Rendering.Pdf;

/// <summary>
/// Shared template assets from <c>{ReportsPath}/_assets</c>. Images and fonts become data URIs and stylesheets
/// become text, so PDF rendering never needs network or file access. Keys are file names with dots and dashes
/// replaced by underscores: <c>logo.svg</c> is <c>assets.logo_svg</c>.
/// </summary>
public sealed class ReportAssets
{
    private static readonly Dictionary<string, string> BinaryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".css", ".html", ".txt" };

    public ReportAssets(IOptions<DocugenOptions> options, IHostEnvironment environment)
        : this(Path.Combine(environment.ContentRootPath, options.Value.ReportsPath, "_assets"))
    {
    }

    internal ReportAssets(string folder)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                var key = Path.GetFileName(file).Replace('.', '_').Replace('-', '_').Replace(' ', '_');
                if (BinaryTypes.TryGetValue(extension, out var mediaType))
                {
                    values[key] = $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}";
                }
                else if (TextTypes.Contains(extension))
                {
                    values[key] = File.ReadAllText(file);
                }
            }
        }

        Values = values;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
}
