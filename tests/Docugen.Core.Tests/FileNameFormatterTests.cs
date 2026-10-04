using Docugen.Core.Parameters;
using Docugen.Core.Reports;

namespace Docugen.Core.Tests;

public class FileNameFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 14, 5, 6, TimeSpan.FromHours(7));

    [Fact]
    public void Replaces_parameter_and_now_tokens()
    {
        BoundParameter[] parameters =
        [
            new("FromDate", ParameterType.Date, new DateOnly(2026, 1, 1)),
            new("Region", ParameterType.String, "North"),
        ];

        var name = FileNameFormatter.Format("sales-{Region}-{FromDate:yyyyMMdd}-{Now:yyyy}", "sales", parameters, Now, ".pdf");

        Assert.Equal("sales-North-20260101-2026.pdf", name);
    }

    [Fact]
    public void Defaults_to_report_key_and_timestamp()
    {
        Assert.Equal("sales-20261004-140506.csv", FileNameFormatter.Format(null, "sales", [], Now, ".csv"));
    }

    [Fact]
    public void Removes_characters_that_are_unsafe_in_file_names()
    {
        BoundParameter[] parameters = [new("Name", ParameterType.String, "../../etc/passwd\"<x>")];

        var name = FileNameFormatter.Format("report-{Name}", "report", parameters, Now, ".xlsx");

        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\"", name);
        Assert.DoesNotContain("<", name);
        Assert.EndsWith(".xlsx", name);
    }

    [Fact]
    public void Null_parameters_become_empty_and_blank_names_fall_back_to_the_key()
    {
        BoundParameter[] parameters = [new("Region", ParameterType.String, null)];

        Assert.Equal("sales.csv", FileNameFormatter.Format("{Region}", "sales", parameters, Now, ".csv"));
    }
}
