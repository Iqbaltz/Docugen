namespace Docugen.Core;

/// <summary>Settings bound from the <c>Docugen</c> configuration section.</summary>
public sealed class DocugenOptions
{
    public const string SectionName = "Docugen";

    /// <summary>
    /// Docugen's own database: report definitions, template assets, export jobs and their event log.
    /// Keep it separate from the data sources reports read from.
    /// </summary>
    public DataSourceOptions Store { get; set; } = new();

    /// <summary>Folder for files being rendered before they are sent. Defaults to the system temp folder.</summary>
    public string? TempPath { get; set; }

    /// <summary>Culture for reports that do not set one.</summary>
    public string DefaultCulture { get; set; } = "en-US";

    public Dictionary<string, DataSourceOptions> DataSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CatalogOptions Catalog { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();

    public CsvRenderingOptions Csv { get; set; } = new();

    public PdfRenderingOptions Pdf { get; set; } = new();
}

public sealed class DataSourceOptions
{
    /// <summary><c>SqlServer</c> (default) or <c>Sqlite</c> (local demos and tests only).</summary>
    public string Provider { get; set; } = "SqlServer";

    public string ConnectionString { get; set; } = "";

    /// <summary>Sqlite only: script that creates the database when its file does not exist yet.</summary>
    public string? SeedScript { get; set; }
}

public sealed class CatalogOptions
{
    /// <summary>How often report definitions and assets are re-read from the store, so changes need no restart.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class LimitOptions
{
    public long MaxRows { get; set; } = 500_000;

    public long MaxPdfRows { get; set; } = 5_000;

    public int DefaultCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Export calls processed at the same time; further calls wait for a free slot.</summary>
    public int MaxConcurrentExports { get; set; } = 8;

    /// <summary>Export calls allowed to wait for a slot. Beyond that, calls are rejected with RESOURCE_EXHAUSTED.</summary>
    public int MaxQueuedExports { get; set; } = 50;
}

public sealed class CsvRenderingOptions
{
    public string Delimiter { get; set; } = ",";

    /// <summary>Write a UTF-8 byte order mark so Excel detects the encoding.</summary>
    public bool IncludeBom { get; set; } = true;

    /// <summary>Prefix text cells starting with = + - @ tab or carriage return with an apostrophe.</summary>
    public bool EscapeFormulas { get; set; } = true;

    /// <summary>Culture for numbers. Empty means invariant (<c>1234.5</c>).</summary>
    public string Culture { get; set; } = "";
}

public sealed class PdfRenderingOptions
{
    /// <summary>Chromium executable to use instead of the browser Playwright downloads.</summary>
    public string? BrowserExecutablePath { get; set; }

    public int MaxConcurrentRenders { get; set; } = 4;

    public int RenderTimeoutSeconds { get; set; } = 120;
}
