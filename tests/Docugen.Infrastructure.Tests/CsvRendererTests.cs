using System.Text;
using Docugen.Core.Data;
using Docugen.Infrastructure.Rendering;

namespace Docugen.Infrastructure.Tests;

public class CsvRendererTests
{
    private static readonly ColumnInfo[] Columns =
    [
        new("Name", "Customer Name", typeof(string)),
        new("Date", "Order Date", typeof(DateTime)),
        new("Total", "Total", typeof(decimal)),
        new("Active", "Active", typeof(bool)),
    ];

    [Fact]
    public async Task Writes_header_rows_bom_and_rfc4180_quoting()
    {
        var csv = await RenderAsync(TestSupport.Options(), Columns,
            ["Acme, Inc.", new DateTime(2026, 3, 1), 1234.5m, true],
            ["Say \"hi\"\nthere", new DateTime(2026, 3, 1, 14, 5, 0), -10m, null]);

        Assert.Equal(
            "Customer Name,Order Date,Total,Active\r\n" +
            "\"Acme, Inc.\",2026-03-01,1234.5,true\r\n" +
            "\"Say \"\"hi\"\"\nthere\",2026-03-01 14:05:00,-10,\r\n",
            csv.Text);
        Assert.True(csv.HasBom);
    }

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+62 811", "'+62 811")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@import", "'@import")]
    [InlineData("plain", "plain")]
    public async Task Escapes_text_that_spreadsheets_would_run_as_a_formula(string value, string expected)
    {
        var csv = await RenderAsync(TestSupport.Options(), [new("V", "V", typeof(string))], [value]);

        Assert.Equal($"V\r\n{expected}\r\n", csv.Text);
    }

    [Fact]
    public async Task Leaves_negative_numbers_alone()
    {
        var csv = await RenderAsync(TestSupport.Options(), [new("N", "N", typeof(int))], [-5]);

        Assert.Equal("N\r\n-5\r\n", csv.Text);
    }

    [Fact]
    public async Task Honours_delimiter_bom_and_formula_settings()
    {
        var options = TestSupport.Options(o =>
        {
            o.Csv.Delimiter = ";";
            o.Csv.IncludeBom = false;
            o.Csv.EscapeFormulas = false;
            o.Csv.Culture = "id-ID";
        });

        var csv = await RenderAsync(options, [new("A", "A", typeof(string)), new("B", "B", typeof(decimal))], ["=x", 1.5m]);

        Assert.Equal("A;B\r\n=x;1,5\r\n", csv.Text);
        Assert.False(csv.HasBom);
    }

    private static async Task<(string Text, bool HasBom)> RenderAsync(Microsoft.Extensions.Options.IOptions<Core.DocugenOptions> options, ColumnInfo[] columns, params object?[][] rows)
    {
        var renderer = new CsvRenderer(options);
        using var output = new MemoryStream();
        await renderer.RenderAsync(TestSupport.Context(), TestSupport.Data(columns, rows), output, TestContext.Current.CancellationToken);
        var bytes = output.ToArray();
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        return (Encoding.UTF8.GetString(hasBom ? bytes[3..] : bytes), hasBom);
    }
}
