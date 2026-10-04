using Docugen.Core.Data;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Docugen.Infrastructure.Rendering;

/// <summary>Builds the workbook stylesheet: a header style, date styles and one style per column number format.</summary>
internal sealed class ExcelStyles
{
    public const uint Default = 0;
    public const uint Header = 1;
    public const uint Date = 2;
    public const uint DateTime = 3;
    public const uint Time = 4;

    private const uint FirstCustomFormatId = 164;
    private static readonly string[] BuiltInFormats = ["yyyy-mm-dd", "yyyy-mm-dd hh:mm:ss", "[h]:mm:ss"];

    private readonly List<string> _formats = [.. BuiltInFormats];
    private readonly uint?[] _columnStyles;

    public ExcelStyles(IReadOnlyList<ColumnInfo> columns)
    {
        _columnStyles = new uint?[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(columns[i].Format))
            {
                continue;
            }

            var formatIndex = _formats.IndexOf(columns[i].Format!);
            if (formatIndex < 0)
            {
                _formats.Add(columns[i].Format!);
                formatIndex = _formats.Count - 1;
            }

            // Cell formats 2.. map one-to-one onto the number formats list.
            _columnStyles[i] = (uint)(formatIndex + 2);
        }
    }

    /// <summary>The column's own format when it has one, otherwise the style for the value's type.</summary>
    public uint For(int column, uint fallback) => _columnStyles[column] ?? fallback;

    public Stylesheet Build()
    {
        var numberingFormats = new NumberingFormats { Count = (uint)_formats.Count };
        var cellFormats = new CellFormats(
            new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U },
            new CellFormat { NumberFormatId = 0U, FontId = 1U, FillId = 2U, BorderId = 0U, FormatId = 0U, ApplyFont = true, ApplyFill = true });

        for (var i = 0; i < _formats.Count; i++)
        {
            var id = FirstCustomFormatId + (uint)i;
            numberingFormats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = _formats[i] });
            cellFormats.Append(new CellFormat { NumberFormatId = id, FontId = 0U, FillId = 0U, BorderId = 0U, FormatId = 0U, ApplyNumberFormat = true });
        }

        cellFormats.Count = (uint)cellFormats.ChildElements.Count;

        return new Stylesheet(
            numberingFormats,
            new Fonts(
                new Font(new FontSize { Val = 11D }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11D }, new Color { Rgb = HexBinaryValue.FromString("FFFFFFFF") }, new FontName { Val = "Calibri" }))
            { Count = 2U },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = HexBinaryValue.FromString("FF1F4E79") },
                    new BackgroundColor { Indexed = 64U })
                { PatternType = PatternValues.Solid }))
            { Count = 3U },
            new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1U },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0U, FontId = 0U, FillId = 0U, BorderId = 0U }) { Count = 1U },
            cellFormats,
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0U, BuiltinId = 0U }) { Count = 1U });
    }
}
