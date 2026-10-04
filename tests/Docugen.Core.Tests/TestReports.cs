using System.Text.Json;
using Docugen.Core.Reports;

namespace Docugen.Core.Tests;

internal static class TestReports
{
    public static ReportDefinition Create(params ParameterDefinition[] parameters) => new()
    {
        Key = "test-report",
        Name = "Test Report",
        DataSource = "Test",
        Query = "SELECT 1",
        Parameters = parameters,
    };

    public static Dictionary<string, JsonElement> Values(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
}
