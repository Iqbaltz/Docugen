namespace Docugen.Core.Reports;

public sealed record PdfOptions
{
    /// <summary>Paper format understood by Chromium: A3, A4, A5, Letter, Legal, Tabloid.</summary>
    public string PageSize { get; init; } = "A4";

    public bool Landscape { get; init; }

    /// <summary>CSS length applied to every page edge.</summary>
    public string Margin { get; init; } = "15mm";

    /// <summary>Overrides <c>Docugen:Limits:MaxPdfRows</c> for this report.</summary>
    public int? MaxRows { get; init; }

    public bool ShowPageNumbers { get; init; } = true;
}
