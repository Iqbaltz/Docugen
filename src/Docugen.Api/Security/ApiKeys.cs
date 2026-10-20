using System.Security.Cryptography;
using System.Text;

namespace Docugen.Api.Security;

/// <summary>
/// The API keys allowed to call Docugen, one per calling application, from <c>Docugen:ApiKeys</c>
/// (<c>{ "BillingApp": "&lt;secret&gt;", ... }</c>). The name identifies the caller in the export log.
/// </summary>
public sealed class ApiKeys
{
    public const string SectionName = "Docugen:ApiKeys";
    public const int MinimumLength = 16;

    private readonly (string Name, byte[] Hash)[] _keys;

    public ApiKeys(IReadOnlyDictionary<string, string?> keys)
    {
        var errors = new List<string>();
        if (keys.Count == 0)
        {
            errors.Add($"No API keys are configured. Add one per calling application under {SectionName}, e.g. " +
                $"dotnet user-secrets set \"{SectionName}:Postman\" \"<a random secret of at least {MinimumLength} characters>\".");
        }

        foreach (var (name, key) in keys)
        {
            if ((key?.Length ?? 0) < MinimumLength)
            {
                errors.Add($"The API key for '{name}' must be at least {MinimumLength} characters.");
            }
        }

        foreach (var duplicate in keys.Where(k => k.Value is not null).GroupBy(k => k.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"{string.Join(" and ", duplicate.Select(k => $"'{k.Key}'"))} share the same API key; give each application its own.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        _keys = keys.Select(k => (k.Key, Hash(k.Value!))).ToArray();
    }

    public static ApiKeys FromConfiguration(IConfiguration configuration) =>
        new(configuration.GetSection(SectionName).GetChildren().ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase));

    /// <summary>The name of the application <paramref name="key"/> belongs to, or null for an unknown key.</summary>
    /// <remarks>Compares hashes in constant time and checks every key, so timing reveals nothing about the keys.</remarks>
    public string? FindCaller(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var presented = Hash(key);
        string? caller = null;
        foreach (var (name, hash) in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
            {
                caller = name;
            }
        }

        return caller;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
