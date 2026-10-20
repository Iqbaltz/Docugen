-- Docugen store for SQL Server: report definitions, template assets and the export log.
-- Run once against the Docugen database (safe to re-run; existing tables are left alone):
--   sqlcmd -S <server> -d Docugen -i db/sqlserver/docugen-store.sql
-- The service needs SELECT on the Report* tables, and SELECT/INSERT/UPDATE/DELETE on docugen.ReportValidation,
-- docugen.Exports and docugen.ExportEvents. Only report authors need to write the Report* tables.
--
-- A report is one row in Reports plus, as needed:
--   ReportParameters   one row per parameter callers may pass
--   ReportColumns      one row per output column (none = every column the query returns, as-is)
--   ReportPdfSettings  one row with the PDF template and page settings (none = built-in table layout, A4)
-- Deleting a report deletes its other rows. Every change is kept in the *History tables, e.g.
--   SELECT * FROM docugen.Reports FOR SYSTEM_TIME ALL WHERE ReportKey = 'sales-orders'

IF SCHEMA_ID(N'docugen') IS NULL
    EXEC (N'CREATE SCHEMA docugen');

-- What a report is and the SQL it runs. Callers ask for it by ReportKey; IsActive = 0 hides it without deleting it.
IF OBJECT_ID(N'docugen.Reports', N'U') IS NULL
CREATE TABLE docugen.Reports (
    ReportKey             NVARCHAR(64)   NOT NULL CONSTRAINT PK_Reports PRIMARY KEY,
    Name                  NVARCHAR(200)  NOT NULL,
    Description           NVARCHAR(1000) NULL,
    DataSource            NVARCHAR(100)  NOT NULL,                     -- a name under Docugen:DataSources
    CommandType           NVARCHAR(20)   NOT NULL CONSTRAINT DF_Reports_CommandType DEFAULT N'Text', -- Text | StoredProcedure
    Query                 NVARCHAR(MAX)  NOT NULL,                     -- SQL with @Parameters, or a procedure name
    CountQuery            NVARCHAR(MAX)  NULL,                         -- optional SELECT COUNT(*) with the same filters
    CommandTimeoutSeconds INT            NULL,                         -- NULL = Docugen:Limits:DefaultCommandTimeoutSeconds
    Formats               NVARCHAR(50)   NOT NULL CONSTRAINT DF_Reports_Formats DEFAULT N'pdf,xlsx,csv',
    FileName              NVARCHAR(200)  NULL,                         -- e.g. sales-{FromDate:yyyyMMdd}
    IsActive              BIT            NOT NULL CONSTRAINT DF_Reports_IsActive DEFAULT 1,
    UpdatedAtUtc          DATETIME2(3)   NOT NULL CONSTRAINT DF_Reports_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedBy             NVARCHAR(128)  NOT NULL CONSTRAINT DF_Reports_UpdatedBy DEFAULT SUSER_SNAME(),
    ValidFrom             DATETIME2      GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo               DATETIME2      GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT CK_Reports_ReportKey CHECK (ReportKey NOT LIKE N'%[^a-z0-9-]%' COLLATE Latin1_General_100_BIN2 AND LEN(ReportKey) > 0)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = docugen.ReportsHistory));

-- The parameters callers may pass, in the order SortOrder gives. A parameter the caller leaves out is NULL in the
-- SQL, or DefaultValue when one is set.
IF OBJECT_ID(N'docugen.ReportParameters', N'U') IS NULL
CREATE TABLE docugen.ReportParameters (
    ReportKey     NVARCHAR(64)   NOT NULL CONSTRAINT FK_ReportParameters_Reports REFERENCES docugen.Reports (ReportKey) ON DELETE CASCADE,
    Name          NVARCHAR(128)  NOT NULL,                             -- as used in the SQL, without the @
    SortOrder     INT            NOT NULL CONSTRAINT DF_ReportParameters_SortOrder DEFAULT 0,
    Type          NVARCHAR(20)   NOT NULL CONSTRAINT DF_ReportParameters_Type DEFAULT N'string',
    IsRequired    BIT            NOT NULL CONSTRAINT DF_ReportParameters_IsRequired DEFAULT 0,
    Label         NVARCHAR(200)  NULL,                                 -- shown to people; NULL = Name
    Description   NVARCHAR(1000) NULL,
    DefaultValue  NVARCHAR(4000) NULL,                                 -- as text: 100, 2026-01-31, true, North; lists: 1,2,3
    MaxLength     INT            NULL,                                 -- string only
    AllowedValues NVARCHAR(4000) NULL,                                 -- string only, comma-separated: North,South,East,West
    ValidFrom     DATETIME2      GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo       DATETIME2      GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT PK_ReportParameters PRIMARY KEY (ReportKey, Name),
    CONSTRAINT CK_ReportParameters_Type CHECK (Type IN (N'string', N'int', N'long', N'decimal', N'bool', N'date', N'datetime', N'string[]', N'int[]'))
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = docugen.ReportParametersHistory));

-- The output columns, in the order SortOrder gives: headers, Excel number formats and widths. A report without rows
-- here exports every column its query returns, with the SQL column names as headers.
IF OBJECT_ID(N'docugen.ReportColumns', N'U') IS NULL
CREATE TABLE docugen.ReportColumns (
    ReportKey NVARCHAR(64)  NOT NULL CONSTRAINT FK_ReportColumns_Reports REFERENCES docugen.Reports (ReportKey) ON DELETE CASCADE,
    Field     NVARCHAR(128) NOT NULL,                                  -- column name as the query returns it
    SortOrder INT           NOT NULL CONSTRAINT DF_ReportColumns_SortOrder DEFAULT 0,
    Header    NVARCHAR(200) NULL,                                      -- NULL = Field
    Format    NVARCHAR(100) NULL,                                      -- Excel number format, e.g. #,##0.00 or dd-mmm-yyyy
    Width     FLOAT         NULL,                                      -- Excel width in characters
    ValidFrom DATETIME2     GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo   DATETIME2     GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT PK_ReportColumns PRIMARY KEY (ReportKey, Field)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = docugen.ReportColumnsHistory));

-- How the PDF looks. Optional: without a row the built-in table layout is used on A4 portrait.
IF OBJECT_ID(N'docugen.ReportPdfSettings', N'U') IS NULL
CREATE TABLE docugen.ReportPdfSettings (
    ReportKey       NVARCHAR(64)  NOT NULL CONSTRAINT PK_ReportPdfSettings PRIMARY KEY
                                  CONSTRAINT FK_ReportPdfSettings_Reports REFERENCES docugen.Reports (ReportKey) ON DELETE CASCADE,
    Template        NVARCHAR(MAX) NULL,                                -- Liquid/HTML; NULL = built-in table layout
    PageSize        NVARCHAR(10)  NOT NULL CONSTRAINT DF_ReportPdfSettings_PageSize DEFAULT N'A4',
    Landscape       BIT           NOT NULL CONSTRAINT DF_ReportPdfSettings_Landscape DEFAULT 0,
    Margin          NVARCHAR(20)  NOT NULL CONSTRAINT DF_ReportPdfSettings_Margin DEFAULT N'15mm',
    MaxRows         INT           NULL,                                -- NULL = Docugen:Limits:MaxPdfRows
    ShowPageNumbers BIT           NOT NULL CONSTRAINT DF_ReportPdfSettings_ShowPageNumbers DEFAULT 1,
    Culture         NVARCHAR(20)  NULL,                                -- number/date formatting, e.g. id-ID; NULL = Docugen:DefaultCulture
    ValidFrom       DATETIME2     GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo         DATETIME2     GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = docugen.ReportPdfSettingsHistory));

-- Files templates can use: assets.<file name with dots and dashes as underscores>, e.g. logo.svg -> assets.logo_svg.
-- Put CSS and SVG in TextContent, PNG/JPEG/fonts in BinaryContent.
IF OBJECT_ID(N'docugen.ReportAssets', N'U') IS NULL
CREATE TABLE docugen.ReportAssets (
    FileName      NVARCHAR(200)  NOT NULL CONSTRAINT PK_ReportAssets PRIMARY KEY,
    TextContent   NVARCHAR(MAX)  NULL,
    BinaryContent VARBINARY(MAX) NULL,
    UpdatedAtUtc  DATETIME2(3)   NOT NULL CONSTRAINT DF_ReportAssets_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedBy     NVARCHAR(128)  NOT NULL CONSTRAINT DF_ReportAssets_UpdatedBy DEFAULT SUSER_SNAME(),
    CONSTRAINT CK_ReportAssets_Content CHECK ((TextContent IS NULL AND BinaryContent IS NOT NULL) OR (TextContent IS NOT NULL AND BinaryContent IS NULL))
);

-- Written by the service: whether each report passed its checks, and why not. Check here after changing a report:
--   SELECT * FROM docugen.ReportValidation WHERE IsValid = 0
IF OBJECT_ID(N'docugen.ReportValidation', N'U') IS NULL
CREATE TABLE docugen.ReportValidation (
    ReportKey    NVARCHAR(64)  NOT NULL CONSTRAINT PK_ReportValidation PRIMARY KEY,
    IsValid      BIT           NOT NULL,
    Errors       NVARCHAR(MAX) NULL,
    Warnings     NVARCHAR(MAX) NULL,
    CheckedAtUtc DATETIME2(3)  NOT NULL
);

-- One row per Export call: what was asked for, by whom, and how it ended. This is the export history.
-- A row still 'Running' long after StartedAtUtc belongs to an instance that crashed mid-export.
IF OBJECT_ID(N'docugen.Exports', N'U') IS NULL
BEGIN
    CREATE TABLE docugen.Exports (
        ExportId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Exports PRIMARY KEY NONCLUSTERED,
        ReportKey      NVARCHAR(64)     NOT NULL,
        Format         NVARCHAR(10)     NOT NULL,             -- pdf | xlsx | csv
        Parameters     NVARCHAR(MAX)    NULL,                 -- bound values as JSON
        CallerId       NVARCHAR(200)    NOT NULL,             -- token subject of the caller
        RequestedBy    NVARCHAR(200)    NULL,                 -- readable caller name (user or client app)
        Status         NVARCHAR(20)     NOT NULL,             -- Running | Succeeded | Failed | Cancelled
        RowsExported   BIGINT           NOT NULL,
        TotalRows      BIGINT           NULL,                 -- from the count query, when the report has one
        FileName       NVARCHAR(260)    NULL,
        ContentType    NVARCHAR(100)    NULL,
        FileSize       BIGINT           NULL,
        ErrorCode      NVARCHAR(50)     NULL,                 -- TOO_MANY_ROWS | QUERY_TIMEOUT | DEFINITION_MISMATCH | INTERNAL
        ErrorMessage   NVARCHAR(2000)   NULL,
        StartedAtUtc   DATETIME2(3)     NOT NULL,
        CompletedAtUtc DATETIME2(3)     NULL,
        DurationMs     BIGINT           NULL
    );
    CREATE CLUSTERED INDEX IX_Exports_StartedAtUtc ON docugen.Exports (StartedAtUtc);
    CREATE INDEX IX_Exports_ReportKey_StartedAtUtc ON docugen.Exports (ReportKey, StartedAtUtc);
    CREATE INDEX IX_Exports_Status_StartedAtUtc ON docugen.Exports (Status, StartedAtUtc);
END;

-- What happened during each export and when: Started, CountingRows, Counted, Querying, WritingRows, Finalizing,
-- Generated, then Sent / Failed / Cancelled. Failures carry the full error text here; callers only see a short
-- message.
IF OBJECT_ID(N'docugen.ExportEvents', N'U') IS NULL
BEGIN
    CREATE TABLE docugen.ExportEvents (
        EventId       BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_ExportEvents PRIMARY KEY,
        ExportId      UNIQUEIDENTIFIER      NOT NULL CONSTRAINT FK_ExportEvents_Exports REFERENCES docugen.Exports (ExportId),
        OccurredAtUtc DATETIME2(3)          NOT NULL,
        Level         NVARCHAR(12)          NOT NULL,          -- Information | Warning | Error
        EventType     NVARCHAR(30)          NOT NULL,
        Message       NVARCHAR(4000)        NOT NULL
    );
    CREATE INDEX IX_ExportEvents_ExportId ON docugen.ExportEvents (ExportId, EventId);
END;

-- Each event alongside its export, for tracking an export end to end:
--   SELECT * FROM docugen.ExportTimeline WHERE ExportId = '...' ORDER BY OccurredAtUtc, EventId
EXEC (N'CREATE OR ALTER VIEW docugen.ExportTimeline AS
SELECT x.ExportId, x.ReportKey, x.Format, x.RequestedBy, x.Status, x.StartedAtUtc,
       e.EventId, e.OccurredAtUtc, e.Level, e.EventType, e.Message
FROM docugen.Exports x
JOIN docugen.ExportEvents e ON e.ExportId = x.ExportId');