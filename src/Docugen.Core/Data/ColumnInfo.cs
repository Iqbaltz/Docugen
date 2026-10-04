namespace Docugen.Core.Data;

/// <summary>An output column: the query field it reads plus presentation hints from the report definition.</summary>
public sealed record ColumnInfo(string Field, string Header, Type DataType, string? Format = null, double? Width = null);
