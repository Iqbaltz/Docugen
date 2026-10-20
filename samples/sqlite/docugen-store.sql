-- Docugen store for Sqlite (demos without SQL Server, and the tests), with the sample reports.
-- Mirrors db/sqlserver/docugen-store.sql and samples/sqlserver/sample-reports.sql. Sqlite has no schemas, so the
-- tables are unqualified. Docugen runs this when Docugen:Store:SeedScript points here and the file does not exist.

CREATE TABLE Reports (
  ReportKey             TEXT    NOT NULL PRIMARY KEY,
  Name                  TEXT    NOT NULL,
  Description           TEXT    NULL,
  DataSource            TEXT    NOT NULL,
  CommandType           TEXT    NOT NULL DEFAULT 'Text',
  Query                 TEXT    NOT NULL,
  CountQuery            TEXT    NULL,
  CommandTimeoutSeconds INTEGER NULL,
  AllowedRoles          TEXT    NULL,
  Formats               TEXT    NOT NULL DEFAULT 'pdf,xlsx,csv',
  Parameters            TEXT    NULL CHECK (Parameters IS NULL OR json_valid(Parameters)),
  Columns               TEXT    NULL CHECK (Columns IS NULL OR json_valid(Columns)),
  FileName              TEXT    NULL,
  Culture               TEXT    NULL,
  PdfTemplate           TEXT    NULL,
  PdfPageSize           TEXT    NOT NULL DEFAULT 'A4',
  PdfLandscape          INTEGER NOT NULL DEFAULT 0,
  PdfMargin             TEXT    NOT NULL DEFAULT '15mm',
  PdfMaxRows            INTEGER NULL,
  PdfShowPageNumbers    INTEGER NOT NULL DEFAULT 1,
  IsActive              INTEGER NOT NULL DEFAULT 1,
  UpdatedAtUtc          TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f', 'now')),
  UpdatedBy             TEXT    NOT NULL DEFAULT 'sqlite'
);

CREATE TABLE ReportAssets (
  FileName      TEXT NOT NULL PRIMARY KEY,
  TextContent   TEXT NULL,
  BinaryContent BLOB NULL,
  UpdatedAtUtc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f', 'now')),
  UpdatedBy     TEXT NOT NULL DEFAULT 'sqlite',
  CHECK ((TextContent IS NULL) <> (BinaryContent IS NULL))
);

CREATE TABLE ReportValidation (
  ReportKey    TEXT    NOT NULL PRIMARY KEY,
  IsValid      INTEGER NOT NULL,
  Errors       TEXT    NULL,
  Warnings     TEXT    NULL,
  CheckedAtUtc TEXT    NOT NULL
);

CREATE TABLE Exports (
  ExportId       TEXT    NOT NULL PRIMARY KEY,
  ReportKey      TEXT    NOT NULL,
  Format         TEXT    NOT NULL,
  Parameters     TEXT    NULL,
  CallerId       TEXT    NOT NULL,
  RequestedBy    TEXT    NULL,
  Status         TEXT    NOT NULL,
  RowsExported   INTEGER NOT NULL,
  TotalRows      INTEGER NULL,
  FileName       TEXT    NULL,
  ContentType    TEXT    NULL,
  FileSize       INTEGER NULL,
  ErrorCode      TEXT    NULL,
  ErrorMessage   TEXT    NULL,
  StartedAtUtc   TEXT    NOT NULL,
  CompletedAtUtc TEXT    NULL,
  DurationMs     INTEGER NULL
);
CREATE INDEX IX_Exports_ReportKey_StartedAtUtc ON Exports (ReportKey, StartedAtUtc);

CREATE TABLE ExportEvents (
  EventId       INTEGER PRIMARY KEY AUTOINCREMENT,
  ExportId      TEXT    NOT NULL REFERENCES Exports (ExportId),
  OccurredAtUtc TEXT    NOT NULL,
  Level         TEXT    NOT NULL,
  EventType     TEXT    NOT NULL,
  Message       TEXT    NOT NULL
);
CREATE INDEX IX_ExportEvents_ExportId ON ExportEvents (ExportId, EventId);

CREATE VIEW ExportTimeline AS
SELECT x.ExportId, x.ReportKey, x.Format, x.RequestedBy, x.Status, x.StartedAtUtc,
       e.EventId, e.OccurredAtUtc, e.Level, e.EventType, e.Message
FROM Exports x
JOIN ExportEvents e ON e.ExportId = x.ExportId;

-- Customer List: All customers with their region and contact email.
INSERT INTO Reports
    (ReportKey, Name, Description, DataSource, Query, CountQuery, CommandTimeoutSeconds, AllowedRoles, Formats,
     Parameters, Columns, FileName, Culture, PdfTemplate, PdfMaxRows)
VALUES (
    'customer-list',
    'Customer List',
    'All customers with their region and contact email.',
    'SalesDb',
    'SELECT
    c.Name AS [Customer],
    c.City,
    r.Name AS [Region],
    c.Email,
    c.CreatedOn AS [Customer Since]
FROM Customers c
JOIN Regions r ON r.Id = c.RegionId
ORDER BY c.Name',
    NULL,
    NULL,
    NULL,
    'pdf,xlsx,csv',
    NULL,
    NULL,
    'customers-{Now:yyyyMMdd}',
    NULL,
    NULL,
    NULL);

-- Sales Orders: Orders placed in the selected period, with totals.
INSERT INTO Reports
    (ReportKey, Name, Description, DataSource, Query, CountQuery, CommandTimeoutSeconds, AllowedRoles, Formats,
     Parameters, Columns, FileName, Culture, PdfTemplate, PdfMaxRows)
VALUES (
    'sales-orders',
    'Sales Orders',
    'Orders placed in the selected period, with totals.',
    'SalesDb',
    'SELECT
    o.OrderDate,
    o.OrderNumber,
    c.Name AS Customer,
    r.Name AS Region,
    o.Quantity,
    o.Total
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Regions r ON r.Id = c.RegionId
WHERE o.OrderDate >= @FromDate
  AND o.OrderDate <= @ToDate
  AND (@Region IS NULL OR r.Name = @Region)
ORDER BY o.OrderDate, o.OrderNumber',
    'SELECT COUNT(*)
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Regions r ON r.Id = c.RegionId
WHERE o.OrderDate >= @FromDate
  AND o.OrderDate <= @ToDate
  AND (@Region IS NULL OR r.Name = @Region)',
    60,
    'Sales,Finance',
    'pdf,xlsx,csv',
    '[
  {"name":"FromDate","label":"From","type":"date","required":true},
  {"name":"ToDate","label":"To","type":"date","required":true},
  {"name":"Region","label":"Region","type":"string","allowedValues":["North","South","East","West"]}
]',
    '[
  {"field":"OrderDate","header":"Order Date","format":"dd-mmm-yyyy","width":14},
  {"field":"OrderNumber","header":"Order No."},
  {"field":"Customer","header":"Customer","width":28},
  {"field":"Region","header":"Region"},
  {"field":"Quantity","header":"Qty","format":"#,##0"},
  {"field":"Total","header":"Total","format":"#,##0.00","width":14}
]',
    'sales-orders-{FromDate:yyyyMMdd}-{ToDate:yyyyMMdd}',
    'en-US',
    '<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <title>{{ report.name }}</title>
  <style>{{ assets.report_css | raw }}</style>
</head>
<body>
  {% assign total_quantity = 0 %}
  {% assign total_amount = 0 %}
  {% for r in rows %}
    {% assign total_quantity = total_quantity | plus: r.Quantity %}
    {% assign total_amount = total_amount | plus: r.Total %}
  {% endfor %}

  <header class="report-header">
    <img class="logo" src="{{ assets.logo_svg }}" alt="">
    <div>
      <h1>{{ report.name }}</h1>
      <p class="subtitle">
        {{ parameters.FromDate | format_date: "dd MMM yyyy" }} &ndash; {{ parameters.ToDate | format_date: "dd MMM yyyy" }}
        &middot; Region: {% if parameters.Region %}{{ parameters.Region }}{% else %}All regions{% endif %}
      </p>
    </div>
    <div class="generated">Generated {{ generated_at_display }}<br>{{ generated_by }}</div>
  </header>

  <section class="cards">
    <div class="card"><span>Orders</span><strong>{{ row_count | format_number: "N0" }}</strong></div>
    <div class="card"><span>Units</span><strong>{{ total_quantity | format_number: "N0" }}</strong></div>
    <div class="card accent"><span>Revenue</span><strong>{{ total_amount | format_number: "N2" }}</strong></div>
  </section>

  <table class="data">
    <thead>
      <tr>
        <th>Date</th><th>Order No.</th><th>Customer</th><th>Region</th><th class="num">Qty</th><th class="num">Total</th>
      </tr>
    </thead>
    <tbody>
      {% for r in rows %}
      <tr>
        <td>{{ r.OrderDate | format_date: "dd MMM yyyy" }}</td>
        <td>{{ r.OrderNumber }}</td>
        <td>{{ r.Customer }}</td>
        <td>{{ r.Region }}</td>
        <td class="num">{{ r.Quantity | format_number: "N0" }}</td>
        <td class="num">{{ r.Total | format_number: "N2" }}</td>
      </tr>
      {% else %}
      <tr><td colspan="6" class="empty">No orders in this period.</td></tr>
      {% endfor %}
      {% if row_count > 0 %}
      <tr class="total">
        <td colspan="4">Total</td>
        <td class="num">{{ total_quantity | format_number: "N0" }}</td>
        <td class="num">{{ total_amount | format_number: "N2" }}</td>
      </tr>
      {% endif %}
    </tbody>
  </table>
</body>
</html>
',
    5000);

-- Shared template assets: assets.logo_svg and assets.report_css in templates.
INSERT INTO ReportAssets (FileName, TextContent) VALUES ('logo.svg', '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="40" viewBox="0 0 200 40">
  <rect x="0" y="4" width="32" height="32" rx="7" fill="#1f4e79"/>
  <text x="16" y="25" text-anchor="middle" font-family="Arial, sans-serif" font-size="14" font-weight="700" fill="#ffffff">TB</text>
  <text x="42" y="27" font-family="Arial, sans-serif" font-size="18" font-weight="700" fill="#1f4e79">TBIG</text>
</svg>');

INSERT INTO ReportAssets (FileName, TextContent) VALUES ('report.css', '* { box-sizing: border-box; }
body { margin: 0; font-family: "Segoe UI", "Noto Sans", "Liberation Sans", Arial, sans-serif; font-size: 9pt; color: #1f2937; }
.report-header { display: flex; align-items: center; gap: 14px; padding-bottom: 10px; border-bottom: 2px solid #1f4e79; }
.report-header .logo { height: 40px; }
.report-header h1 { margin: 0; font-size: 17pt; color: #1f4e79; }
.report-header .subtitle { margin: 2px 0 0; color: #4b5563; }
.report-header .generated { margin-left: auto; text-align: right; font-size: 8pt; color: #6b7280; }
.cards { display: flex; gap: 10px; margin: 12px 0; }
.card { flex: 1; border: 1px solid #e5e7eb; border-radius: 6px; padding: 8px 10px; }
.card span { display: block; font-size: 7.5pt; text-transform: uppercase; letter-spacing: .04em; color: #6b7280; }
.card strong { font-size: 14pt; font-variant-numeric: tabular-nums; }
.card.accent { background: #1f4e79; border-color: #1f4e79; color: #fff; }
.card.accent span { color: #cfe0f1; }
table.data { width: 100%; border-collapse: collapse; }
table.data thead { display: table-header-group; }
table.data th { background: #f1f5f9; color: #1f2937; text-align: left; font-weight: 600; padding: 5px 6px; border-bottom: 1px solid #cbd5e1; }
table.data td { padding: 4px 6px; border-bottom: 1px solid #eef2f6; }
table.data tr { break-inside: avoid; }
table.data tr.total td { font-weight: 700; border-top: 2px solid #1f4e79; border-bottom: none; padding-top: 6px; }
.num, table.data .num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
.empty { text-align: center; color: #6b7280; padding: 24px; }');
