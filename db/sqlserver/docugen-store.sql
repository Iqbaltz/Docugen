-- Docugen store for SQL Server: report definitions, template assets and the export log.
-- Run once against the Docugen database (safe to re-run; existing tables are left alone):
--   sqlcmd -S <server> -d Docugen -i db/sqlserver/docugen-store.sql
-- The service needs SELECT on docugen.Reports and docugen.ReportAssets, and SELECT/INSERT/UPDATE/DELETE on
-- docugen.ReportValidation, docugen.Exports and docugen.ExportEvents. Only report authors need to write Reports.

IF SCHEMA_ID(N'docugen') IS NULL
    EXEC (N'CREATE SCHEMA docugen');

-- One row per report. Callers run a report by ReportKey; IsActive = 0 hides it without deleting it.
-- System-versioned: every change is kept in docugen.ReportsHistory, e.g.
--   SELECT * FROM docugen.Reports FOR SYSTEM_TIME ALL WHERE ReportKey = 'sales-orders'
IF OBJECT_ID(N'docugen.Reports', N'U') IS NULL
CREATE TABLE docugen.Reports (
    ReportKey             NVARCHAR(64)   NOT NULL CONSTRAINT PK_Reports PRIMARY KEY,
    Name                  NVARCHAR(200)  NOT NULL,
    Description           NVARCHAR(1000) NULL,
    DataSource            NVARCHAR(100)  NOT NULL,                     -- a name under Docugen:DataSources
    CommandType           NVARCHAR(20)   NOT NULL CONSTRAINT DF_Reports_CommandType DEFAULT N'Text', -- Text | StoredProcedure
    Query                 NVARCHAR(MAX)  NOT NULL,                     -- SQL with @Parameters, or a procedure name
    CountQuery            NVARCHAR(MAX)  NULL,                         -- optional SELECT COUNT(*) with the same filters
    CommandTimeoutSeconds INT            NULL,
    AllowedRoles          NVARCHAR(1000) NULL,                         -- comma-separated; NULL = any signed-in caller
    Formats               NVARCHAR(50)   NOT NULL CONSTRAINT DF_Reports_Formats DEFAULT N'pdf,xlsx,csv',
    Parameters            NVARCHAR(MAX)  NULL,                         -- JSON array of parameter definitions
    Columns               NVARCHAR(MAX)  NULL,                         -- JSON array of output columns; NULL = all
    FileName              NVARCHAR(200)  NULL,                         -- e.g. sales-{FromDate:yyyyMMdd}
    Culture               NVARCHAR(20)   NULL,                         -- e.g. id-ID; NULL = Docugen:DefaultCulture
    PdfTemplate           NVARCHAR(MAX)  NULL,                         -- Liquid/HTML; NULL = built-in table layout
    PdfPageSize           NVARCHAR(10)   NOT NULL CONSTRAINT DF_Reports_PdfPageSize DEFAULT N'A4',
    PdfLandscape          BIT            NOT NULL CONSTRAINT DF_Reports_PdfLandscape DEFAULT 0,
    PdfMargin             NVARCHAR(20)   NOT NULL CONSTRAINT DF_Reports_PdfMargin DEFAULT N'15mm',
    PdfMaxRows            INT            NULL,                         -- NULL = Docugen:Limits:MaxPdfRows
    PdfShowPageNumbers    BIT            NOT NULL CONSTRAINT DF_Reports_PdfShowPageNumbers DEFAULT 1,
    IsActive              BIT            NOT NULL CONSTRAINT DF_Reports_IsActive DEFAULT 1,
    UpdatedAtUtc          DATETIME2(3)   NOT NULL CONSTRAINT DF_Reports_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedBy             NVARCHAR(128)  NOT NULL CONSTRAINT DF_Reports_UpdatedBy DEFAULT SUSER_SNAME(),
    ValidFrom             DATETIME2      GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo               DATETIME2      GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT CK_Reports_ReportKey CHECK (ReportKey NOT LIKE N'%[^a-z0-9-]%' COLLATE Latin1_General_100_BIN2 AND LEN(ReportKey) > 0),
    CONSTRAINT CK_Reports_Parameters CHECK (Parameters IS NULL OR ISJSON(Parameters) = 1),
    CONSTRAINT CK_Reports_Columns CHECK (Columns IS NULL OR ISJSON(Columns) = 1)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = docugen.ReportsHistory));

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