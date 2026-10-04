using System.Globalization;
using System.Text.Json;
using Docugen.Core.Reports;

namespace Docugen.Core.Parameters;

/// <summary>
/// Converts caller-supplied JSON values into typed parameter values for a report.
/// Every declared parameter is always bound (null when omitted) so queries can use <c>@Name IS NULL</c>.
/// </summary>
public static class ParameterBinder
{
    public static ParameterBindingResult Bind(ReportDefinition report, IReadOnlyDictionary<string, JsonElement>? values)
    {
        values ??= new Dictionary<string, JsonElement>();
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var bound = new List<BoundParameter>(report.Parameters.Count);

        foreach (var name in values.Keys)
        {
            if (!report.Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                AddError(errors, name, "Unknown parameter.");
            }
        }

        foreach (var definition in report.Parameters)
        {
            var supplied = TryGetValue(values, definition.Name, out var element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (!supplied && definition.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fallback)
            {
                element = fallback;
                supplied = true;
            }

            if (!supplied)
            {
                if (definition.Required)
                {
                    AddError(errors, definition.Name, "A value is required.");
                }

                bound.Add(new BoundParameter(definition.Name, definition.Type, null));
                continue;
            }

            if (TryConvert(definition, element, out var value, out var error))
            {
                bound.Add(new BoundParameter(definition.Name, definition.Type, value));
            }
            else
            {
                AddError(errors, definition.Name, error);
            }
        }

        return new ParameterBindingResult(
            bound,
            errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, JsonElement> values, string name, out JsonElement element)
    {
        if (values.TryGetValue(name, out element))
        {
            return true;
        }

        foreach (var (key, value) in values)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                element = value;
                return true;
            }
        }

        return false;
    }

    private static bool TryConvert(ParameterDefinition definition, JsonElement element, out object? value, out string error)
    {
        error = "";
        value = null;

        switch (definition.Type)
        {
            case ParameterType.StringArray:
            case ParameterType.IntArray:
                if (element.ValueKind != JsonValueKind.Array)
                {
                    error = "Expected an array.";
                    return false;
                }

                var itemType = definition.Type == ParameterType.IntArray ? ParameterType.Int : ParameterType.String;
                var items = new List<object>();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryConvertScalar(definition with { Type = itemType }, item, out var converted, out error) || converted is null)
                    {
                        error = $"Array item: {(error.Length > 0 ? error : "null is not allowed.")}";
                        return false;
                    }

                    items.Add(converted);
                }

                value = definition.Type == ParameterType.IntArray
                    ? items.Cast<int>().ToArray()
                    : items.Cast<string>().ToArray();
                return true;

            default:
                return TryConvertScalar(definition, element, out value, out error);
        }
    }

    private static bool TryConvertScalar(ParameterDefinition definition, JsonElement element, out object? value, out string error)
    {
        error = "";
        value = null;
        var text = element.ValueKind == JsonValueKind.String ? element.GetString()! : null;

        switch (definition.Type)
        {
            case ParameterType.String:
                if (text is null)
                {
                    error = "Expected a string.";
                    return false;
                }

                if (definition.MaxLength is { } max && text.Length > max)
                {
                    error = $"Must be at most {max} characters.";
                    return false;
                }

                if (definition.AllowedValues is { Count: > 0 } allowed)
                {
                    var match = allowed.FirstOrDefault(a => a.Equals(text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        error = $"Must be one of: {string.Join(", ", allowed)}.";
                        return false;
                    }

                    text = match;
                }

                value = text;
                return true;

            case ParameterType.Int:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var i)
                    || text is not null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                {
                    value = i;
                    return true;
                }

                error = "Expected a whole number.";
                return false;

            case ParameterType.Long:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var l)
                    || text is not null && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                {
                    value = l;
                    return true;
                }

                error = "Expected a whole number.";
                return false;

            case ParameterType.Decimal:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d)
                    || text is not null && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out d))
                {
                    value = d;
                    return true;
                }

                error = "Expected a number.";
                return false;

            case ParameterType.Bool:
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = element.GetBoolean();
                    return true;
                }

                if (text is not null && bool.TryParse(text, out var b))
                {
                    value = b;
                    return true;
                }

                error = "Expected true or false.";
                return false;

            case ParameterType.Date:
                if (text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    value = date;
                    return true;
                }

                error = "Expected a date in yyyy-MM-dd format.";
                return false;

            case ParameterType.DateTime:
                if (text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
                {
                    value = dateTime;
                    return true;
                }

                error = "Expected an ISO 8601 date and time, e.g. 2026-01-31T13:45:00.";
                return false;

            default:
                error = $"Unsupported parameter type {definition.Type}.";
                return false;
        }
    }

    private static void AddError(Dictionary<string, List<string>> errors, string name, string message)
    {
        if (!errors.TryGetValue(name, out var list))
        {
            errors[name] = list = [];
        }

        list.Add(message);
    }
}
