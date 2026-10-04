namespace Docugen.Core.Reports;

public sealed record ColumnDefinition
{
    /// <summary>Column name as returned by the query.</summary>
    public required string Field { get; init; }

    /// <summary>Header text. Defaults to <see cref="Field"/>.</summary>
    public string? Header { get; init; }

    /// <summary>Excel number format applied to the column, e.g. <c>#,##0.00</c> or <c>dd-mmm-yyyy</c>.</summary>
    public string? Format { get; init; }

    /// <summary>Excel column width in characters.</summary>
    public double? Width { get; init; }
}
