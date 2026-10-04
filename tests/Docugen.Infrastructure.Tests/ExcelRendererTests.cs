using Docugen.Core.Data;
using Docugen.Infrastructure.Rendering;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace Docugen.Infrastructure.Tests;

public class ExcelRendererTests
{
    private static readonly ColumnInfo[] Columns =
    [
        new("Name", "Customer", typeof(string)),
        new("OrderDate", "Order Date", typeof(DateTime)),
        new("Quantity", "Qty", typeof(int), Format: "#,##0"),
        new("Total", "Total", typeof(decimal), Format: "#,##0.00", Width: 20),
        new("Paid", "Paid", typeof(bool)),
        new("Note", "Note", typeof(string)),
    ];

    [Fact]
    public async Task Produces_a_valid_workbook_with_typed_cells()
    {
        using var stream = await RenderAsync(
            ["Acme", new DateTime(2026, 3, 1), 1200, 1234.56m, true, "bad\u0001char"],
            ["Beta", new DateTime(2026, 3, 2, 10, 30, 0), 5, -3.5m, false, null]);

        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.Empty(errors);

        var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
        var rows = sheet.GetFirstChild<SheetData>()!.Elements<Row>().ToList();
        Assert.Equal(3, rows.Count);

        var header = rows[0].Elements<Cell>().Select(c => c.InnerText).ToArray();
        Assert.Equal(["Customer", "Order Date", "Qty", "Total", "Paid", "Note"], header);

        var first = rows[1].Elements<Cell>().ToDictionary(c => c.CellReference!.Value!);
        Assert.Equal(CellValues.InlineString, first["A2"].DataType!.Value);
        Assert.Equal(new DateTime(2026, 3, 1).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture), first["B2"].CellValue!.Text);
        Assert.Null(first["C2"].DataType);
        Assert.Equal("1200", first["C2"].CellValue!.Text);
        Assert.Equal("1234.56", first["D2"].CellValue!.Text);
        Assert.Equal(CellValues.Boolean, first["E2"].DataType!.Value);
        Assert.Equal("badchar", first["F2"].InnerText);

        // Null values produce no cell at all.
        Assert.DoesNotContain(rows[2].Elements<Cell>(), c => c.CellReference == "F3");

        var formats = NumberFormats(document);
        Assert.Equal("yyyy-mm-dd", formats[first["B2"].StyleIndex!.Value]);
        Assert.Equal("#,##0", formats[first["C2"].StyleIndex!.Value]);
        Assert.Equal("#,##0.00", formats[first["D2"].StyleIndex!.Value]);

        var secondDate = rows[2].Elements<Cell>().First(c => c.CellReference == "B3");
        Assert.Equal("yyyy-mm-dd hh:mm:ss", formats[secondDate.StyleIndex!.Value]);
    }

    [Fact]
    public async Task Freezes_and_filters_the_header_and_sizes_columns()
    {
        using var stream = await RenderAsync(["Acme", new DateTime(2026, 3, 1), 1, 1m, true, "x"]);

        using var document = SpreadsheetDocument.Open(stream, false);
        var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;

        var pane = sheet.Descendants<Pane>().Single();
        Assert.Equal(PaneStateValues.Frozen, pane.State!.Value);
        Assert.Equal("A2", pane.TopLeftCell!.Value);
        Assert.Equal("A1:F2", sheet.GetFirstChild<AutoFilter>()!.Reference!.Value);
        Assert.Equal(20, sheet.Descendants<Column>().ElementAt(3).Width!.Value);
        Assert.Equal("Test Report", document.WorkbookPart.Workbook!.Descendants<Sheet>().Single().Name!.Value);
    }

    [Fact]
    public async Task Writes_a_header_only_sheet_when_there_are_no_rows()
    {
        using var stream = await RenderAsync();

        using var document = SpreadsheetDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken));
        var rows = document.WorkbookPart!.WorksheetParts.Single().Worksheet!.Descendants<Row>().ToList();
        Assert.Single(rows);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void Converts_column_indexes_to_letters(int index, string expected) =>
        Assert.Equal(expected, ExcelRenderer.ColumnName(index));

    [Theory]
    [InlineData("Sales [Q1]: North/South", "Sales  Q1   North South")]
    [InlineData("A very long report name that exceeds the limit", "A very long report name that ex")]
    [InlineData("''", "Report")]
    public void Builds_valid_sheet_names(string reportName, string expected) =>
        Assert.Equal(expected, ExcelRenderer.SheetName(reportName));

    private static async Task<MemoryStream> RenderAsync(params object?[][] rows)
    {
        var stream = new MemoryStream();
        await new ExcelRenderer().RenderAsync(TestSupport.Context(), TestSupport.Data(Columns, rows), stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        return stream;
    }

    private static Dictionary<uint, string> NumberFormats(SpreadsheetDocument document)
    {
        var stylesheet = document.WorkbookPart!.WorkbookStylesPart!.Stylesheet!;
        var codes = stylesheet.NumberingFormats!.Elements<NumberingFormat>().ToDictionary(n => n.NumberFormatId!.Value, n => n.FormatCode!.Value!);
        return stylesheet.CellFormats!.Elements<CellFormat>()
            .Select((format, index) => (index, id: format.NumberFormatId?.Value ?? 0))
            .Where(x => codes.ContainsKey(x.id))
            .ToDictionary(x => (uint)x.index, x => codes[x.id]);
    }
}
