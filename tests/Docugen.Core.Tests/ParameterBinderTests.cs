using System.Text.Json;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;

namespace Docugen.Core.Tests;

public class ParameterBinderTests
{
    [Fact]
    public void Binds_typed_values_from_json()
    {
        var report = TestReports.Create(
            new ParameterDefinition { Name = "FromDate", Type = ParameterType.Date, Required = true },
            new ParameterDefinition { Name = "At", Type = ParameterType.DateTime },
            new ParameterDefinition { Name = "Count", Type = ParameterType.Int },
            new ParameterDefinition { Name = "Big", Type = ParameterType.Long },
            new ParameterDefinition { Name = "Amount", Type = ParameterType.Decimal },
            new ParameterDefinition { Name = "Active", Type = ParameterType.Bool },
            new ParameterDefinition { Name = "Ids", Type = ParameterType.IntArray },
            new ParameterDefinition { Name = "Codes", Type = ParameterType.StringArray });

        var result = ParameterBinder.Bind(report, TestReports.Values(new
        {
            FromDate = "2026-03-01",
            At = "2026-03-01T08:30:00",
            Count = 5,
            Big = "9000000000",
            Amount = 12.5m,
            Active = true,
            Ids = new[] { 1, 2, 3 },
            Codes = new[] { "A", "B" },
        }));

        Assert.True(result.IsValid);
        var values = result.Parameters.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(new DateOnly(2026, 3, 1), values["FromDate"]);
        Assert.Equal(new DateTime(2026, 3, 1, 8, 30, 0), values["At"]);
        Assert.Equal(5, values["Count"]);
        Assert.Equal(9_000_000_000L, values["Big"]);
        Assert.Equal(12.5m, values["Amount"]);
        Assert.Equal(true, values["Active"]);
        Assert.Equal(new[] { 1, 2, 3 }, values["Ids"]);
        Assert.Equal(new[] { "A", "B" }, values["Codes"]);
    }

    [Fact]
    public void Omitted_optional_parameters_are_bound_as_null()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "Region" });

        var result = ParameterBinder.Bind(report, null);

        Assert.True(result.IsValid);
        var parameter = Assert.Single(result.Parameters);
        Assert.Null(parameter.Value);
    }

    [Fact]
    public void Reports_missing_required_unknown_and_invalid_values()
    {
        var report = TestReports.Create(
            new ParameterDefinition { Name = "FromDate", Type = ParameterType.Date, Required = true },
            new ParameterDefinition { Name = "Count", Type = ParameterType.Int });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { Count = "five", Extra = 1 }));

        Assert.False(result.IsValid);
        Assert.Equal(["A value is required."], result.Errors["FromDate"]);
        Assert.Equal(["Expected a whole number."], result.Errors["Count"]);
        Assert.Equal(["Unknown parameter."], result.Errors["Extra"]);
    }

    [Fact]
    public void Explicit_null_counts_as_missing()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "FromDate", Type = ParameterType.Date, Required = true });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { FromDate = (string?)null }));

        Assert.Equal(["A value is required."], result.Errors["FromDate"]);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("01/03/2026")]
    [InlineData("yesterday")]
    public void Rejects_dates_not_in_iso_format(string value)
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "D", Type = ParameterType.Date });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { D = value }));

        Assert.Equal(["Expected a date in yyyy-MM-dd format."], result.Errors["D"]);
    }

    [Fact]
    public void Matches_parameter_names_case_insensitively()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "Region", Required = true });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { region = "North" }));

        Assert.True(result.IsValid);
        Assert.Equal("North", result.Parameters[0].Value);
    }

    [Fact]
    public void Enforces_allowed_values_and_normalizes_their_case()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "Region", AllowedValues = ["North", "South"] });

        var ok = ParameterBinder.Bind(report, TestReports.Values(new { Region = "north" }));
        var bad = ParameterBinder.Bind(report, TestReports.Values(new { Region = "Moon" }));

        Assert.Equal("North", ok.Parameters[0].Value);
        Assert.Equal(["Must be one of: North, South."], bad.Errors["Region"]);
    }

    [Fact]
    public void Enforces_max_length()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "Name", MaxLength = 3 });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { Name = "abcd" }));

        Assert.Equal(["Must be at most 3 characters."], result.Errors["Name"]);
    }

    [Fact]
    public void Uses_default_when_value_is_omitted()
    {
        var report = TestReports.Create(new ParameterDefinition
        {
            Name = "Top",
            Type = ParameterType.Int,
            Required = true,
            Default = JsonDocument.Parse("10").RootElement,
        });

        var result = ParameterBinder.Bind(report, null);

        Assert.True(result.IsValid);
        Assert.Equal(10, result.Parameters[0].Value);
    }

    [Fact]
    public void Rejects_arrays_with_wrong_item_types()
    {
        var report = TestReports.Create(new ParameterDefinition { Name = "Ids", Type = ParameterType.IntArray });

        var result = ParameterBinder.Bind(report, TestReports.Values(new { Ids = new object[] { 1, "x" } }));

        Assert.Equal(["Array item: Expected a whole number."], result.Errors["Ids"]);
    }
}
