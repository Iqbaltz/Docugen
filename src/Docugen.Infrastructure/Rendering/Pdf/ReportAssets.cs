using System.Text;

namespace Docugen.Infrastructure.Rendering.Pdf;

/// <summary>Shared template assets, exposed to PDF templates as <c>assets.&lt;key&gt;</c>.</summary>
public interface IReportAssets
{
    IReadOnlyDictionary<string, object?> Values { get; }
}

/// <summary>A row of the <c>ReportAssets</c> table: text content (CSS, SVG) or binary content (PNG, fonts).</summary>
public sealed record ReportAsset(string FileName, string? Text, byte[]? Data);

/// <summary>
/// A fixed set of template assets. Images and fonts become data URIs and stylesheets become text, so PDF rendering
/// never needs network or file access. Keys are file names with dots, dashes and spaces replaced by underscores:
/// <c>logo.svg</c> is <c>assets.logo_svg</c>.
/// </summary>
public sealed class ReportAssets : IReportAssets
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

    public static ReportAssets Empty { get; } = new([]);

    public ReportAssets(IEnumerable<ReportAsset> assets)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            var extension = Path.GetExtension(asset.FileName);
            if (BinaryTypes.TryGetValue(extension, out var mediaType))
            {
                var bytes = asset.Data ?? Encoding.UTF8.GetBytes(asset.Text ?? "");
                values[KeyFor(asset.FileName)] = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }
            else if (TextTypes.Contains(extension))
            {
                values[KeyFor(asset.FileName)] = asset.Text ?? Encoding.UTF8.GetString(asset.Data ?? []);
            }
        }

        Values = values;
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>True when templates can use the file: an image, font or stylesheet type.</summary>
    public static bool IsSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return BinaryTypes.ContainsKey(extension) || TextTypes.Contains(extension);
    }

    public static string KeyFor(string fileName) => fileName.Replace('.', '_').Replace('-', '_').Replace(' ', '_');
}
