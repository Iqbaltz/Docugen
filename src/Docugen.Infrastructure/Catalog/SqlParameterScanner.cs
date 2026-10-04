using System.Text;
using System.Text.RegularExpressions;

namespace Docugen.Infrastructure.Catalog;

/// <summary>
/// Finds the <c>@Name</c> placeholders a SQL batch references, ignoring comments, string literals,
/// bracketed identifiers, <c>@@</c> globals and variables the batch declares itself.
/// </summary>
internal static partial class SqlParameterScanner
{
    public static IReadOnlySet<string> FindParameters(string sql)
    {
        var code = StripLiteralsAndComments(sql);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DeclareRegex().Matches(code))
        {
            locals.Add(match.Groups["name"].Value);
        }

        foreach (Match match in DeclareListRegex().Matches(code))
        {
            locals.Add(match.Groups["name"].Value);
        }

        var parameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ParameterRegex().Matches(code))
        {
            var name = match.Groups["name"].Value;
            if (!locals.Contains(name))
            {
                parameters.Add(name);
            }
        }

        return parameters;
    }

    private static string StripLiteralsAndComments(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                builder.Append(' ');
            }
            else if (c is '\'' or '[' or '"')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == close)
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                i++;
                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"(?<![@\w])@(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParameterRegex();

    [GeneratedRegex(@"\bDECLARE\s+@(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareRegex();

    // Further variables in "DECLARE @a int, @b int": a comma, the name, then a type name.
    [GeneratedRegex(@",\s*@(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(AS\s+)?(?!AS\b)[A-Za-z]", RegexOptions.IgnoreCase)]
    private static partial Regex DeclareListRegex();
}
