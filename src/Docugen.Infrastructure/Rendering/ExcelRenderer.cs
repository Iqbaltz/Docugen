using System.Globalization;
using System.Text;
using System.Xml;
using Docugen.Core.Data;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Docugen.Infrastructure.Rendering;

/// <summary>
/// Streams rows into a single-sheet XLSX workbook with the Open XML SAX writer, so memory use stays flat
/// regardless of row count. Values keep their native types (numbers, dates, booleans).
/// </summary>
public sealed class ExcelRenderer : IDocumentRenderer
{
    /// <summary>Excel's row limit minus the header row.</summary>
    public const int MaxDataRows = 1_048_575;

    private const int MaxCellText = 32_767;
    private const int SampleRows = 100;

    public OutputFormat Format => OutputFormat.Xlsx;

    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string FileExtension => ".xlsx";

    public long? GetRowLimit(ReportDefinition report) => MaxDataRows;

    public async Task RenderAsync(RenderContext context, ReportData data, Stream output, CancellationToken cancellationToken)
    {
        var columns = data.Columns;
        var styles = new ExcelStyles(columns);
        var columnNames = Enumerable.Range(0, columns.Count).Select(ColumnName).ToArray();

        await using var rows = data.ReadRowsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        // Look at the first rows to size the columns, then stream everything.
        var sample = new List<object?[]>();
        while (sample.Count < SampleRows && await rows.MoveNextAsync().ConfigureAwait(false))
        {
            sample.Add(rows.Current);
        }

        using var document = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        document.PackageProperties.Title = context.Report.Name;
        document.PackageProperties.Creator = context.GeneratedBy ?? "Docugen";
        document.PackageProperties.Created = context.GeneratedAt.UtcDateTime;

        var workbookPart = document.AddWorkbookPart();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = styles.Build();

        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        uint rowIndex = 1;
        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteElement(FrozenHeaderView());
            if (columns.Count > 0)
            {
                writer.WriteElement(BuildColumns(columns, sample));
            }

            writer.WriteStartElement(new SheetData());
            WriteHeader(writer, columns, columnNames);
            foreach (var row in sample)
            {
                WriteRow(writer, ++rowIndex, row, styles, columnNames);
            }

            while (await rows.MoveNextAsync().ConfigureAwait(false))
            {
                WriteRow(writer, ++rowIndex, rows.Current, styles, columnNames);
            }

            writer.WriteEndElement(); // sheetData

            if (columns.Count > 0)
            {
                writer.WriteElement(new AutoFilter { Reference = $"A1:{columnNames[^1]}{rowIndex}" });
            }

            writer.WriteEndElement(); // worksheet
        }

        var sheetName = SheetName(context.Report.Name);
        var workbook = new Workbook(new Sheets(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1U, Name = sheetName }));
        if (columns.Count > 0)
        {
            workbook.Append(new DefinedNames(new DefinedName($"'{sheetName.Replace("'", "''", StringComparison.Ordinal)}'!$A$1:${columnNames[^1]}${rowIndex}")
            {
                Name = "_xlnm._FilterDatabase",
                LocalSheetId = 0U,
                Hidden = true,
            }));
        }

        workbookPart.Workbook = workbook;
    }

    private static SheetViews FrozenHeaderView() =>
        new(new SheetView(
            new Pane { VerticalSplit = 1D, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
            new Selection { Pane = PaneValues.BottomLeft, ActiveCell = "A2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "A2" } })
        {
            TabSelected = true,
            WorkbookViewId = 0U,
        });

    private static Columns BuildColumns(IReadOnlyList<ColumnInfo> columns, List<object?[]> sample)
    {
        var result = new Columns();
        for (var i = 0; i < columns.Count; i++)
        {
            var width = columns[i].Width ?? Math.Clamp(
                Math.Max(columns[i].Header.Length, sample.Count == 0 ? 0 : sample.Max(r => EstimateLength(r[i]))) + 3,
                8,
                60);
            var index = (uint)(i + 1);
            result.Append(new Column { Min = index, Max = index, Width = width, CustomWidth = true });
        }

        return result;
    }

    private static int EstimateLength(object? value) => value switch
    {
        null => 0,
        string text => text.Length,
        DateTime dateTime => dateTime.TimeOfDay == TimeSpan.Zero ? 11 : 19,
        DateOnly => 11,
        DateTimeOffset => 19,
        decimal or double or float => Convert.ToString(value, CultureInfo.InvariantCulture)!.Length + 3,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Length ?? 0,
    };

    private static void WriteHeader(OpenXmlWriter writer, IReadOnlyList<ColumnInfo> columns, string[] columnNames)
    {
        writer.WriteStartElement(new Row { RowIndex = 1U });
        for (var i = 0; i < columns.Count; i++)
        {
            writer.WriteElement(TextCell($"{columnNames[i]}1", columns[i].Header, ExcelStyles.Header));
        }

        writer.WriteEndElement();
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, object?[] values, ExcelStyles styles, string[] columnNames)
    {
        writer.WriteStartElement(new Row { RowIndex = rowIndex });
        var suffix = rowIndex.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < values.Length; i++)
        {
            var cell = CreateCell(columnNames[i] + suffix, values[i], i, styles);
            if (cell is not null)
            {
                writer.WriteElement(cell);
            }
        }

        writer.WriteEndElement();
    }

    private static Cell? CreateCell(string reference, object? value, int column, ExcelStyles styles) => value switch
    {
        null => null,
        string text => TextCell(reference, text, styles.For(column, ExcelStyles.Default)),
        bool flag => new Cell { CellReference = reference, DataType = CellValues.Boolean, CellValue = new CellValue(flag ? "1" : "0"), StyleIndex = styles.For(column, ExcelStyles.Default) },
        double number when double.IsNaN(number) || double.IsInfinity(number) => TextCell(reference, number.ToString(CultureInfo.InvariantCulture), ExcelStyles.Default),
        float number when float.IsNaN(number) || float.IsInfinity(number) => TextCell(reference, number.ToString(CultureInfo.InvariantCulture), ExcelStyles.Default),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            NumberCell(reference, Convert.ToString(value, CultureInfo.InvariantCulture)!, styles.For(column, ExcelStyles.Default)),
        DateTime dateTime => NumberCell(reference, ToOADate(dateTime), styles.For(column, dateTime.TimeOfDay == TimeSpan.Zero ? ExcelStyles.Date : ExcelStyles.DateTime)),
        DateOnly date => NumberCell(reference, ToOADate(date.ToDateTime(TimeOnly.MinValue)), styles.For(column, ExcelStyles.Date)),
        DateTimeOffset offset => NumberCell(reference, ToOADate(offset.DateTime), styles.For(column, ExcelStyles.DateTime)),
        TimeSpan span => NumberCell(reference, span.TotalDays.ToString(CultureInfo.InvariantCulture), styles.For(column, ExcelStyles.Time)),
        TimeOnly time => NumberCell(reference, time.ToTimeSpan().TotalDays.ToString(CultureInfo.InvariantCulture), styles.For(column, ExcelStyles.Time)),
        byte[] bytes => TextCell(reference, Convert.ToBase64String(bytes), styles.For(column, ExcelStyles.Default)),
        _ => TextCell(reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", styles.For(column, ExcelStyles.Default)),
    };

    private static string ToOADate(DateTime dateTime) => dateTime.ToOADate().ToString(CultureInfo.InvariantCulture);

    private static Cell NumberCell(string reference, string value, uint style) =>
        new() { CellReference = reference, CellValue = new CellValue(value), StyleIndex = style == 0 ? null : style };

    private static Cell TextCell(string reference, string text, uint style) =>
        new(new InlineString(new Text(CleanText(text)) { Space = SpaceProcessingModeValues.Preserve }))
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            StyleIndex = style == 0 ? null : style,
        };

    /// <summary>Removes characters XML cannot carry and applies Excel's cell length limit.</summary>
    internal static string CleanText(string text)
    {
        if (text.Length > MaxCellText)
        {
            text = text[..MaxCellText];
        }

        var valid = true;
        for (var i = 0; i < text.Length && valid; i++)
        {
            valid = XmlConvert.IsXmlChar(text[i]) || (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]) && ++i > 0);
        }

        if (valid)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (XmlConvert.IsXmlChar(text[i]))
            {
                builder.Append(text[i]);
            }
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                builder.Append(text[i]).Append(text[i + 1]);
                i++;
            }
        }

        return builder.ToString();
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    internal static string SheetName(string reportName)
    {
        var builder = new StringBuilder();
        foreach (var c in reportName)
        {
            builder.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? ' ' : c);
        }

        var name = builder.ToString().Trim().Trim('\'');
        if (name.Length > 31)
        {
            name = name[..31].TrimEnd();
        }

        return name.Length == 0 ? "Report" : name;
    }
}
