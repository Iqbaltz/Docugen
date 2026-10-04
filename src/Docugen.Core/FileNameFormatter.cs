using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Docugen.Core.Parameters;

namespace Docugen.Core;

/// <summary>Builds safe download file names from a report's <c>fileName</c> pattern.</summary>
public static partial class FileNameFormatter
{
    private const int MaxLength = 150;

    public static string Format(string? pattern, string reportKey, IReadOnlyList<BoundParameter> parameters, DateTimeOffset now, string extension)
    {
        pattern = string.IsNullOrWhiteSpace(pattern) ? reportKey + "-{Now:yyyyMMdd-HHmmss}" : pattern;

        var name = TokenRegex().Replace(pattern, match =>
        {
            var token = match.Groups["name"].Value;
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;

            if (token.Equals("Now", StringComparison.OrdinalIgnoreCase))
            {
                return now.ToString(format ?? "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            }

            var parameter = parameters.FirstOrDefault(p => p.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            return parameter?.Value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
                System.Collections.IEnumerable items and not string => string.Join("_", items.Cast<object>()),
                var value => value.ToString() ?? "",
            };
        });

        return Sanitize(name, reportKey) + extension;
    }

    private static string Sanitize(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsControl(c) || invalid.Contains(c) || c is '"' or '\'' or '/' or '\\' or ':' or '*' or '?' or '<' or '>' or '|' ? '_' : c);
        }

        var result = builder.ToString().Trim(' ', '.', '_', '-');
        if (result.Length > MaxLength)
        {
            result = result[..MaxLength];
        }

        return result.Length == 0 ? fallback : result;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(:(?<format>[^}]+))?\}")]
    private static partial Regex TokenRegex();
}
