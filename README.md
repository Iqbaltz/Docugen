# Docugen

Docugen is an internal **gRPC service** that turns **named SQL Server reports** into **PDF, Excel (XLSX) and CSV** files, for any application that needs documents. A caller makes one `Export` call and gets back a stream: progress from 0 to 100 while the report runs, then the file. Docugen keeps no state between calls; if an export should run in the background, the calling application runs the call from its own background job. It is built on .NET 10 and ASP.NET Core gRPC.

Reports are **rows in Docugen's own database** (the *store*): the SQL, the parameters callers may pass, the output columns and, optionally, an HTML template for the PDF. Developers add a report with a few `INSERT`s; Docugen picks it up within 30 seconds, without a restart or deployment. Callers identify themselves with an **API key**, ask for a report by its key and pass parameter values. **They never send SQL.**

```
Export { report_key: "sales-orders", format: XLSX, parameters: { "FromDate": "2026-01-01", "ToDate": "2026-03-31" } }
  → stream: progress QUERYING 5%, progress WRITING_ROWS 37%, …, progress COMPLETED 100%,
            file { file_name: "sales-orders-20260101-20260331.xlsx", size: 6812, export_id: "01a153a8-…" },
            chunk, chunk, …
```

## How it works

```
Export ─► API key ─► catalog ─► parameter binding ─► export slot ─► SQL Server ─► renderer ─► temp file ─► stream to caller
          x-api-key   Report*     typed values,        8 at once,     count query    streamed     CSV  (CsvHelper)
          metadata    tables      INVALID_ARGUMENT     50 waiting     (optional),    rows         XLSX (Open XML SDK)
                                  on bad input                        report query               PDF  (Liquid → Chromium)
                                                                  │
                                                                  └─► progress messages on the same stream; Exports + ExportEvents log
```

| Project | Responsibility |
|---|---|
| `src/Docugen.Contracts` | The gRPC contract: `Protos/docugen/v1/docugen.proto` and the generated C# client and server types. Other .NET apps reference it for a typed client. |
| `src/Docugen.Core` | Report model, parameter binding, the `ReportGenerator` that runs a query and renders it, the export record and progress interface. Uses only Microsoft packages. |
| `src/Docugen.Infrastructure` | The store-backed report catalog and its validation; ADO.NET data access (SQL Server, plus Sqlite for demos); the CSV, XLSX and PDF renderers; the exporter and the export log. |
| `src/Docugen.Api` | The gRPC services, API key authentication, gRPC health checks and server reflection. |
| `db/sqlserver/` | The store schema for SQL Server. |
| `samples/` | Demo sales data (SQL Server and Sqlite), the sample reports for the store, and a ready-made Sqlite store. |

Design choices:

- **No SQL from callers.** The SQL lives in the `Reports` table. Values are always sent to the database as typed `DbParameter`s, never concatenated into the SQL text.
- **One API key per calling application.** A missing or unknown key gets `UNAUTHENTICATED`. Keys are compared in constant time and never logged, and the key's name identifies the caller in the export log.
- **One call, no server-side state.** An export lives exactly as long as its `Export` call. Nothing is queued or stored between calls, so any instance can serve any call and instances can be added behind a load balancer.
- **Protected capacity.** Each instance runs up to 8 exports at once and lets up to 50 more wait for a slot (both configurable); beyond that, callers get `RESOURCE_EXHAUSTED`. PDF printing has its own limit of 4 at once and a time limit.
- **Honest progress.** Progress messages report the stage (queued, counting, querying, writing rows, finalizing) and the rows written so far. A report with a *count query* also gets an exact percentage, and fails fast when it would exceed the row limit.
- **Streaming.** Rows go from the data reader straight into the CSV and XLSX writers, so memory stays flat even for hundreds of thousands of rows. PDF output is capped by default at 5,000 rows, since a PDF that long is unreadable anyway.
- **No half-sent files.** The file is rendered to a temporary file first and only sent once it is complete, so a failure part-way through is an error status, never a truncated file. The temporary file is deleted as soon as the call ends.
- **Every export is logged.** Who asked for what, with which parameters, how long it took, how many rows, and why it failed, in the store's `Exports` and `ExportEvents` tables.
- **Reports change without a restart.** The catalog is re-read every 30 seconds. A broken report is left out (the others keep working) and the reason is written to `ReportValidation`.
- **Free licenses only.** Every dependency is MIT, Apache or BSD licensed and free for commercial use. EPPlus, iText and QuestPDF are deliberately not used.

## Quick start

Development uses SQL Server LocalDB (`(localdb)\MSSQLLocalDB`, installed with Visual Studio or SQL Server Express) for two databases:

- `DocugenSales`: the demo data the sample reports read, through the `SalesDb` data source.
- `Docugen`: the store, which holds report definitions and the export log.

Create both once:

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE DocugenSales"
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d DocugenSales -i samples/sqlserver/seed.sql

sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE Docugen"
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d Docugen -i db/sqlserver/docugen-store.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d Docugen -i samples/sqlserver/sample-reports.sql
```

Without LocalDB (for example on Linux or macOS), use the Sqlite demos instead. Both databases are created from their scripts the first time the service starts:

```bash
cd src/Docugen.Api
dotnet user-secrets set "Docugen:Store:Provider" "Sqlite"
dotnet user-secrets set "Docugen:Store:ConnectionString" "Data Source=../../samples/store.db"
dotnet user-secrets set "Docugen:Store:SeedScript" "../../samples/sqlite/docugen-store.sql"
dotnet user-secrets set "Docugen:DataSources:SalesDb:Provider" "Sqlite"
dotnet user-secrets set "Docugen:DataSources:SalesDb:ConnectionString" "Data Source=../../samples/demo.db"
dotnet user-secrets set "Docugen:DataSources:SalesDb:SeedScript" "../../samples/sqlite/seed.sql"
```

Then:

```bash
# 1. Install Chromium for PDF rendering (one time)
dotnet build
dotnet src/Docugen.Api/bin/Debug/net10.0/Docugen.Api.dll install-browsers

# 2. Create an API key for yourself, stored in user secrets (any random string of 16+ characters)
cd src/Docugen.Api
dotnet user-secrets set "Docugen:ApiKeys:Postman" "<a random secret>"

# 3. Run the service (gRPC over plain HTTP/2 on localhost:5180)
dotnet run
```

If a Chromium build is already installed somewhere else, you can skip step 1. Point Docugen at it with `Docugen:Pdf:BrowserExecutablePath` instead.

To make a random key, run `openssl rand -base64 32`, or in PowerShell: `$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)`. To see your key again, run `dotnet user-secrets list` in `src/Docugen.Api`. The service will not start without at least one key.

### Trying it in Postman

1. **New → gRPC**. Enter `localhost:5180` as the URL and leave TLS off.
2. Under **Service definition**, choose **Using server reflection** (available in Development). This lists `docugen.v1.ReportService` and `docugen.v1.ExportService`.
3. Under **Metadata**, add the key `x-api-key` with your key as the value. Leave the **Authorization** tab set to **No Auth**.
4. Pick `ExportService/Export` and send:
   ```json
   { "report_key": "sales-orders", "format": "OUTPUT_FORMAT_XLSX", "parameters": { "FromDate": "2026-01-01", "ToDate": "2026-03-31" } }
   ```
5. Postman lists the progress messages, then the `file` message, then the chunks (as base64). That is good for checking the call; use a real client to save the file.

## Calling Docugen from another application

Ask the Docugen administrators for an API key for your application, keep it in your app's secret store, and send it on every call in the `x-api-key` metadata.

Call `Export` and read the stream to the end: `progress` messages while the report runs, one `file` message, then `chunk` messages to append in order. To stop an export, cancel the call; Docugen stops the query. **Set a deadline** that fits the report: a large PDF can take a minute or more. For exports that take a while, make the call from a background job in your application and pass the progress on to your users.

.NET apps reference `src/Docugen.Contracts` (or a package built from it). Apps in other languages generate a client from `src/Docugen.Contracts/Protos/docugen/v1/docugen.proto`.

```csharp
using var channel = GrpcChannel.ForAddress("https://docugen.internal");
var exports = new ExportService.ExportServiceClient(channel);
var headers = new Metadata { { "x-api-key", apiKey } };

using var call = exports.Export(new ExportRequest
{
    ReportKey = "sales-orders",
    Format = OutputFormat.Xlsx,
    Parameters = Struct.Parser.ParseJson("""{ "FromDate": "2026-01-01", "ToDate": "2026-03-31" }"""),
}, headers, deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: cancellationToken);

FileStream? file = null;
try
{
    await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken))
    {
        switch (message.PayloadCase)
        {
            case ExportResponse.PayloadOneofCase.Progress:
                Console.WriteLine($"{message.Progress.Stage} {message.Progress.Percent}% ({message.Progress.RowsWritten} rows)");
                break;
            case ExportResponse.PayloadOneofCase.File:
                file = File.Create(message.File.FileName);
                break;
            case ExportResponse.PayloadOneofCase.Chunk:
                message.Chunk.WriteTo(file!);
                break;
        }
    }
}
catch (RpcException ex)
{
    // ex.StatusCode says what kind of error; ex.GetRpcStatus()?.GetDetail<ErrorInfo>() has the reason
    // (e.g. TOO_MANY_ROWS) and metadata["export_id"] to quote to the Docugen administrators.
    throw;
}
finally
{
    file?.Dispose();
}
```

## Adding a report

A report is one row in `docugen.Reports`, plus rows in three small tables as needed:

| Table | Rows | Leave it empty and… |
|---|---|---|
| `docugen.Reports` | one per report: its key, name, data source and SQL | (required) |
| `docugen.ReportParameters` | one per parameter callers may pass | the report takes no parameters |
| `docugen.ReportColumns` | one per output column: order, header, Excel format and width | every column the query returns is exported, with its SQL name as the header |
| `docugen.ReportPdfSettings` | one with the PDF template and page settings | the built-in table layout on A4 portrait |

Copy a report from `samples/sqlserver/sample-reports.sql` to start. Docugen loads it at the next refresh (within `Catalog:RefreshInterval`, 30 seconds by default). Then check that it passed validation:

```sql
INSERT INTO docugen.Reports (ReportKey, Name, Description, DataSource, Query, CountQuery, FileName)
VALUES (
    N'sales-by-region',
    N'Sales by Region',
    N'Order totals per region for a period.',
    N'SalesDb',
    N'SELECT r.Name AS Region, COUNT(*) AS Orders, SUM(o.Quantity) AS Quantity, SUM(o.Total) AS Total
      FROM Orders o
      JOIN Customers c ON c.Id = o.CustomerId
      JOIN Regions r ON r.Id = c.RegionId
      WHERE o.OrderDate BETWEEN @FromDate AND @ToDate
      GROUP BY r.Name
      ORDER BY Total DESC',
    N'SELECT COUNT(DISTINCT c.RegionId)
      FROM Orders o JOIN Customers c ON c.Id = o.CustomerId
      WHERE o.OrderDate BETWEEN @FromDate AND @ToDate',
    N'sales-by-region-{FromDate:yyyyMMdd}-{ToDate:yyyyMMdd}');

INSERT INTO docugen.ReportParameters (ReportKey, Name, SortOrder, Type, IsRequired, Label) VALUES
    (N'sales-by-region', N'FromDate', 1, N'date', 1, N'From'),
    (N'sales-by-region', N'ToDate',   2, N'date', 1, N'To');

INSERT INTO docugen.ReportColumns (ReportKey, Field, SortOrder, Format) VALUES
    (N'sales-by-region', N'Region',   1, NULL),
    (N'sales-by-region', N'Orders',   2, N'#,##0'),
    (N'sales-by-region', N'Quantity', 3, N'#,##0'),
    (N'sales-by-region', N'Total',    4, N'#,##0.00');

-- Half a minute later:
SELECT * FROM docugen.ReportValidation WHERE ReportKey = N'sales-by-region';
```

To change a report, `UPDATE` its rows. To retire one without deleting it, set `Reports.IsActive = 0`. Deleting a `Reports` row also deletes its parameters, columns and PDF settings. All four tables are system-versioned, so every earlier version is kept: `SELECT * FROM docugen.ReportParameters FOR SYSTEM_TIME ALL WHERE ReportKey = N'sales-by-region'`.

### `docugen.Reports`

| Column | Required | Meaning |
|---|---|---|
| `ReportKey` | yes | What callers ask for: lower-case letters, digits and dashes. |
| `Name`, `Description` | `Name` | Shown by `ListReports` / `GetReport` and in PDFs. |
| `DataSource` | yes | A name under `Docugen:DataSources` in configuration. Connection strings stay in configuration, never in the table. |
| `CommandType` | | `Text` (default) or `StoredProcedure`. With `StoredProcedure`, `Query` holds the procedure name. |
| `Query` | yes | The SQL, with `@Parameter` placeholders. Every placeholder needs a `ReportParameters` row. |
| `CountQuery` | | `SELECT COUNT(*) …` with the same filters. Gives exact progress, and over-limit exports fail before the main query runs. |
| `CommandTimeoutSeconds` | | Defaults to `Limits:DefaultCommandTimeoutSeconds` (60). |
| `Formats` | | Comma-separated, default `pdf,xlsx,csv`. |
| `FileName` | | Download name without extension. Supports `{Param}`, `{Param:format}` and `{Now:format}`. |
| `IsActive` | | `0` hides the report. |

### `docugen.ReportParameters`

| Column | Required | Meaning |
|---|---|---|
| `ReportKey`, `Name` | yes | The report, and the parameter name as used in the SQL, without the `@`. |
| `SortOrder` | | Order in `GetReport` and in PDFs' `parameter_list`. |
| `Type` | | One of the types below; default `string`. |
| `IsRequired` | | `1` rejects calls that leave the parameter out. |
| `Label`, `Description` | | Shown to people; `Label` defaults to `Name`. |
| `DefaultValue` | | Used when the caller leaves the parameter out, written as text: `100`, `2026-01-31`, `true`, `North`; for lists, `1,2,3`. |
| `MaxLength`, `AllowedValues` | | For `string`: a length limit, and a comma-separated list of allowed values such as `North,South,East,West`. |

### `docugen.ReportColumns`

| Column | Required | Meaning |
|---|---|---|
| `ReportKey`, `Field` | yes | The report, and the column name as the query returns it. |
| `SortOrder` | | Output order. |
| `Header` | | Column header; defaults to `Field`. |
| `Format` | | Excel number format, e.g. `#,##0.00` or `dd-mmm-yyyy`. |
| `Width` | | Excel column width in characters. |

### `docugen.ReportPdfSettings`

| Column | Default | Meaning |
|---|---|---|
| `ReportKey` | | The report. |
| `Template` | built-in table layout | Liquid/HTML template; see [PDF templates](#pdf-templates). |
| `PageSize`, `Landscape`, `Margin` | `A4`, `0`, `15mm` | Paper format, orientation and the margin on every edge. |
| `MaxRows` | `Limits:MaxPdfRows` (5,000) | Most rows a PDF of this report may have. |
| `ShowPageNumbers` | `1` | "Page X of Y" in the footer. |
| `Culture` | `Docugen:DefaultCulture` | Number and date formatting in the PDF, e.g. `id-ID`. |

The tables reject a malformed `ReportKey` and an unknown parameter `Type` when you insert. Everything else (the data source exists, every `@Param` has a parameter row, defaults match their types, the template compiles) is checked by Docugen and reported in `docugen.ReportValidation`.

### Parameter types

| Type | Value in the request | Sent to SQL Server as |
|---|---|---|
| `string` | `"text"` (checked against `MaxLength` and `AllowedValues`) | `nvarchar(4000)` |
| `int`, `long`, `decimal` | number, or a numeric string | `int`, `bigint`, `decimal` |
| `bool` | `true` / `false` | `bit` |
| `date` | `"2026-01-31"` | `date` |
| `datetime` | `"2026-01-31T13:45:00"` | `datetime2` |
| `int[]`, `string[]` | `[1, 2, 3]` | JSON string; read it with `OPENJSON(@Ids)` |

A parameter the caller leaves out is passed as `NULL` (or its `DefaultValue`, if one is set). This makes optional filters straightforward:

```sql
WHERE o.OrderDate BETWEEN @FromDate AND @ToDate
  AND (@Region IS NULL OR r.Name = @Region)
  AND (@RegionIds IS NULL OR r.Id IN (SELECT CAST(value AS int) FROM OPENJSON(@RegionIds)))
```

gRPC sends numbers as doubles, so whole numbers above 2^53 should be sent as strings (`"9007199254740993"`), which every numeric type accepts.
## PDF templates

Templates use [Liquid](https://shopify.github.io/liquid/) syntax, rendered by [Fluid](https://github.com/sebastienros/fluid), and are printed by headless Chromium. That means ordinary HTML and CSS work, including `@page`, `break-before: page` and table headers that repeat on every page. **Every `{{ value }}` is HTML-encoded automatically.** Use `{{ value | raw }}` only for trusted content such as the shared CSS.

| Variable | Contents |
|---|---|
| `report.key`, `report.name`, `report.description` | From the `Reports` row |
| `parameters.<Name>` | Bound parameter values (dates as dates, `nil` when omitted) |
| `parameter_list` | `[{ name, label, display }]`, convenient for printing the active filters |
| `columns` | `[{ field, header, kind, numeric, format }]`; `kind` is one of `integer`, `number`, `date`, `datetime`, `bool`, `text` |
| `rows` | One object per row, keyed by field: `{{ r.Total }}` |
| `display_rows` | The same rows as lists of preformatted strings (what the built-in template uses) |
| `row_count`, `generated_at`, `generated_at_display`, `generated_by` | Run information; `generated_by` is the caller's name |
| `assets.<file_name>` | Rows of `docugen.ReportAssets`, with dots and dashes turned into underscores. Images and fonts are data URIs (`assets.logo_svg`); CSS is text (`assets.report_css`) |

Put CSS and SVG in `ReportAssets.TextContent`, and PNG, JPEG and font files in `BinaryContent`. Useful filters: `format_number: "N2"` and `format_date: "dd MMM yyyy"`, which both use .NET format strings and the report's culture. Also `plus` for running totals, and `default`. The `sales-orders` sample has a complete template with summary cards and a totals row.

Chromium runs with JavaScript disabled and **all network requests blocked**. Put images, fonts and CSS in `ReportAssets` rather than linking to URLs. Page numbers ("Page X of Y") are added in the footer unless `ReportPdfSettings.ShowPageNumbers` is `0`.

## gRPC API

Package `docugen.v1`; the full contract with comments is in [docugen.proto](src/Docugen.Contracts/Protos/docugen/v1/docugen.proto). Every call needs an API key in the `x-api-key` metadata except `grpc.health.v1.Health`.

| Service / method | Description |
|---|---|
| `ReportService/ListReports` | The active reports |
| `ReportService/GetReport` | Parameters, formats and columns of one report (the SQL is not exposed); `exact_progress` tells whether it has a count query |
| `ExportService/Export` | Runs a report. Streams `progress` messages, then one `file` message (name, content type, size, row count, export id), then 64 KiB `chunk`s |
| `grpc.health.v1.Health/Check` | Checks the store and every data source can be reached |

Progress is sent on every stage change and at most every quarter of a second while rows are written. The percentage is 0 while waiting for a slot, 2 while counting, 5 while querying, then 10 to 90 as rows are written (in proportion to `total_rows`), 90 while the file is completed (for PDFs, while Chromium prints) and 100 just before the file is sent. **Without a count query the total is unknown, so the percentage stays at 10 while rows are written;** `rows_written` still moves.

Errors before the export starts:

| Status code | When |
|---|---|
| `INVALID_ARGUMENT` | Missing or unsupported format, or unknown, missing or invalid parameters. The details carry a `google.rpc.BadRequest` with one violation per field (`parameters.FromDate`, `format`) |
| `UNAUTHENTICATED` | The `x-api-key` metadata is missing or holds an unknown key |
| `NOT_FOUND` | Unknown report |
| `RESOURCE_EXHAUSTED` | Too many exports are running and waiting on this instance (`Limits:MaxConcurrentExports`, `Limits:MaxQueuedExports`); retry with backoff |

Errors while it runs carry a `google.rpc.ErrorInfo` with domain `docugen`, the reason below and `metadata["export_id"]`:

| Status code | Reason | Meaning |
|---|---|---|
| `FAILED_PRECONDITION` | `TOO_MANY_ROWS` | More rows than the format allows (PDF limit, Excel's 1,048,575 rows, or `Limits:MaxRows`) |
| `DEADLINE_EXCEEDED` | `QUERY_TIMEOUT` | The SQL query took longer than its command timeout |
| `INTERNAL` | `DEFINITION_MISMATCH` | `ReportColumns` lists a field the query does not return |
| `INTERNAL` | `INTERNAL` | Anything else, e.g. PDF printing took longer than `Pdf:RenderTimeoutSeconds`; the full error is in the export log |

A call cancelled by the caller, or whose deadline passes, ends with the usual `CANCELLED` / `DEADLINE_EXCEEDED` and is logged as `Cancelled`.

## Tracking exports

Every `Export` call that gets a slot is recorded in the store, whether it succeeds or not:

- **`docugen.Exports`**: one row per export: report, format, parameter values, which application asked (`RequestedBy`: the name of its API key), status (`Running`, `Succeeded`, `Failed`, `Cancelled`), rows, file size, error, start time and duration.
- **`docugen.ExportEvents`**: what happened and when: `Started`, `CountingRows`, `Counted`, `Querying`, `WritingRows`, `Finalizing`, `Generated`, then `Sent` / `Failed` / `Cancelled`. Failures carry the full exception text here; callers only see a short message.
- **`docugen.ExportTimeline`**: a view of the two side by side.

```sql
-- Recent exports
SELECT TOP 50 StartedAtUtc, ReportKey, Format, RequestedBy, Status, RowsExported, DurationMs, ErrorCode
FROM docugen.Exports ORDER BY StartedAtUtc DESC;

-- Everything that happened to one export (the export_id from the file message or the error details)
SELECT OccurredAtUtc, Level, EventType, Message
FROM docugen.ExportTimeline WHERE ExportId = '01a153a8-5e46-716f-8ec1-b3cdc0293d15'
ORDER BY OccurredAtUtc, EventId;

-- Usage and failures per calling application over the last week
SELECT RequestedBy, ReportKey, Status, COUNT(*) AS Exports, AVG(DurationMs) AS AvgMs
FROM docugen.Exports WHERE StartedAtUtc > DATEADD(day, -7, SYSUTCDATETIME())
GROUP BY RequestedBy, ReportKey, Status ORDER BY Exports DESC;
```

A row still `Running` long after its `StartedAtUtc` belongs to an instance that crashed mid-export. The log grows by one row (plus a handful of events) per export; schedule a purge of old rows (for example, older than 90 days) to suit your retention policy.

The service also writes an `AUDIT` log entry for every export it sends, with the caller, report, format, row count, export id and parameter values.

## Configuration

Settings live in `appsettings.json` and can be overridden by environment variables (use `__` for nesting, e.g. `Docugen__Store__ConnectionString`).

```jsonc
{
  "Kestrel": { "EndpointDefaults": { "Protocols": "Http2" } },   // gRPC without TLS; with TLS use Http1AndHttp2
  "Docugen": {
    "ApiKeys": {                                                // one per calling application; keep the values in secrets, not here
      "BillingApp": "<secret>",
      "HrPortal": "<secret>"
    },
    "Store": {                                                    // Docugen's own database
      "Provider": "SqlServer",
      "ConnectionString": "Server=sql01;Database=Docugen;Authentication=Active Directory Default;Encrypt=True"
    },
    "TempPath": null,                                             // where files are rendered before sending; default: system temp
    "DefaultCulture": "en-US",
    "DataSources": {                                              // what reports read from; names used in Reports.DataSource
      "SalesDb": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=sql01;Database=Sales;Authentication=Active Directory Default;Encrypt=True;ApplicationIntent=ReadOnly"
      }
    },
    "Catalog": { "RefreshInterval": "00:00:30" },
    "Limits": {
      "MaxRows": 500000, "MaxPdfRows": 5000, "DefaultCommandTimeoutSeconds": 60,
      "MaxConcurrentExports": 8,                                  // per instance
      "MaxQueuedExports": 50                                      // calls allowed to wait for a slot; more get RESOURCE_EXHAUSTED
    },
    "Csv": { "Delimiter": ",", "IncludeBom": true, "EscapeFormulas": true, "Culture": "" },
    "Pdf": { "BrowserExecutablePath": null, "MaxConcurrentRenders": 4, "RenderTimeoutSeconds": 120 }
  }
}
```

Each calling application gets its own entry in `Docugen:ApiKeys`: the name is what the export log shows as `RequestedBy`, the value is a random secret of at least 16 characters. To add an application, add a key and restart the service; to revoke one, remove its key. Set keys through environment variables (`Docugen__ApiKeys__BillingApp`), a key vault or `dotnet user-secrets`, never in `appsettings.json`.

## Security checklist

- **Whoever can write `docugen.Reports` can make Docugen run any SQL its data-source logins allow.** Grant `INSERT`/`UPDATE`/`DELETE` on the `Report*` tables and `ReportAssets` to report authors only, and review changes (the history tables show what changed and when).
- **Every application with an API key can run every report.** Give keys only to applications that should see the data behind all the reports, give each application its own key, and remove a key as soon as an application no longer needs it.
- Treat API keys like passwords: random, at least 32 characters for production, stored in a secret store on both sides, and sent only over TLS (terminate TLS in front of Docugen).
- Connect each data source with a **read-only SQL login** (`db_datareader`, or `EXECUTE` on specific procedures only). Add `ApplicationIntent=ReadOnly` to send queries to a readable secondary.
- Keep the store in its own database (or at least its own schema and login). The service's store login needs `SELECT` on the `Report*` tables and `ReportAssets` and read/write on `ReportValidation`, `Exports` and `ExportEvents`; it should not be able to read the business data.
- Keep connection strings out of source control and out of the store: use environment variables, a key vault or `dotnet user-secrets`.
- Text in CSV files that starts with `=`, `+`, `-`, `@`, a tab or a carriage return gets an apostrophe in front (protection against CSV/formula injection). Numbers are left untouched. XLSX cells are typed, so the problem does not arise there.
- PDF templates cannot run JavaScript or reach the network, and data values are HTML-encoded.

## Deployment

```bash
docker build -t docugen .
docker run -p 8080:8080 \
  -e Docugen__ApiKeys__BillingApp="<secret>" \
  -e Docugen__Store__ConnectionString="Server=...;Database=Docugen;..." \
  -e Docugen__DataSources__SalesDb__ConnectionString="Server=...;Database=Sales;..." \
  docugen
```

The image is built on `mcr.microsoft.com/dotnet/aspnet:10.0` and adds a Chromium build that matches the Playwright package, plus fonts. It serves gRPC over plain HTTP/2 on port 8080; terminate TLS at an ingress or load balancer that supports gRPC (HTTP/2 to the backend). Use the gRPC health service (`grpc.health.v1.Health`) for liveness and readiness probes. Run `db/sqlserver/docugen-store.sql` against the store database before the first start.

Instances share nothing but the store, so you can run several behind a load balancer. The export limits apply per instance: with 3 instances, up to 24 exports run at once. Size instances for PDF printing, which needs the most CPU and memory (one Chromium per instance, `Pdf:MaxConcurrentRenders` pages at a time).

To deploy without Docker (on Windows or Linux), run `dotnet publish src/Docugen.Api -c Release`, then run `Docugen.Api install-browsers` once on the server.

## Development

```bash
dotnet build
dotnet test        # unit, renderer, exporter, gRPC and SQL Server integration tests
```

- PDF tests print real documents. Set `DOCUGEN_BROWSER_PATH` to use an existing Chromium binary, or run `install-browsers` as shown above.
- `SqlServerIntegrationTests` runs the store scripts, the sample reports and the export log against SQL Server 2022 in Docker (Testcontainers), and checks the sample report returns the same rows as the Sqlite demo for every format. Without Docker, point them at any SQL Server with `DOCUGEN_TEST_SQLSERVER`, e.g. `Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True`; they create and drop a throwaway database. Otherwise they skip themselves.
- When you change the store schema, change `db/sqlserver/docugen-store.sql` and `samples/sqlite/docugen-store.sql` together.
- CI (`.github/workflows/ci.yml`) builds the solution, installs Chromium, runs every test and builds the Docker image.

## Roadmap

- Per-application report access (which API key may run which reports), and sign-in with Entra ID instead of API keys when needed.
- Per-application limits, so one busy caller cannot take every export slot.
- Metrics and tracing (OpenTelemetry): export duration, failures and slot waits per report and per caller.
- Publish `Docugen.Contracts` as a NuGet package for .NET callers.
- A preview call that returns the first N rows, for building report screens.
- Admin calls to create and validate reports, for authors who should not have direct table access.
