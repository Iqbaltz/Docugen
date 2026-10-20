# Docugen

Docugen is an internal **gRPC service** that turns **named SQL Server reports** into **PDF, Excel (XLSX) and CSV** files, for any application that needs documents. A caller makes one `Export` call and gets back a stream: progress from 0 to 100 while the report runs, then the file. Docugen keeps no state between calls; if an export should run in the background, the calling application runs the call from its own background job. It is built on .NET 10 and ASP.NET Core gRPC.

Reports are **rows in Docugen's own database** (the *store*): the SQL, the parameters callers may pass, who may run it and, optionally, an HTML template for the PDF. Developers add a report with an `INSERT`; Docugen picks it up within 30 seconds, without a restart or deployment. Callers ask for a report by its key and pass parameter values. **They never send SQL.**

```
Export { report_key: "sales-orders", format: XLSX, parameters: { "FromDate": "2026-01-01", "ToDate": "2026-03-31" } }
  → stream: progress QUERYING 5%, progress WRITING_ROWS 37%, …, progress COMPLETED 100%,
            file { file_name: "sales-orders-20260101-20260331.xlsx", size: 6812, export_id: "01a153a8-…" },
            chunk, chunk, …
```

## How it works

```
Export ─► catalog ─► access check ─► parameter binding ─► export slot ─► SQL Server ─► renderer ─► temp file ─► stream to caller
          Reports      AllowedRoles     typed values,        8 at once,     count query    streamed     CSV  (CsvHelper)
          table        vs. token roles  INVALID_ARGUMENT     50 waiting     (optional),    rows         XLSX (Open XML SDK)
                                        on bad input                        report query               PDF  (Liquid → Chromium)
                                                                  │
                                                                  └─► progress messages on the same stream; Exports + ExportEvents log
```

| Project | Responsibility |
|---|---|
| `src/Docugen.Contracts` | The gRPC contract: `Protos/docugen/v1/docugen.proto` and the generated C# client and server types. Other .NET apps reference it for a typed client. |
| `src/Docugen.Core` | Report model, parameter binding, the `ReportGenerator` that runs a query and renders it, the export record and progress interface. Uses only Microsoft packages. |
| `src/Docugen.Infrastructure` | The store-backed report catalog and its validation; ADO.NET data access (SQL Server, plus Sqlite for demos); the CSV, XLSX and PDF renderers; the exporter and the export log. |
| `src/Docugen.Api` | The gRPC services, JWT authentication, gRPC health checks and server reflection. |
| `db/sqlserver/` | The store schema for SQL Server. |
| `samples/` | Demo sales data (SQL Server and Sqlite), the sample reports for the store, and a ready-made Sqlite store. |

Design choices:

- **No SQL from callers.** The SQL lives in the `Reports` table. Values are always sent to the database as typed `DbParameter`s, never concatenated into the SQL text.
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

# 2. Create a development token (the caller must be in role Sales or Finance to run sales-orders)
cd src/Docugen.Api
dotnet user-jwts create --name alice@example.com --role Sales --output token

# 3. Run the service (gRPC over plain HTTP/2 on localhost:5180)
dotnet run
```

If a Chromium build is already installed somewhere else, you can skip step 1. Point Docugen at it with `Docugen:Pdf:BrowserExecutablePath` instead.

### Trying it in Postman

1. **New → gRPC**. Enter `localhost:5180` as the URL and leave TLS off.
2. Under **Service definition**, choose **Using server reflection** (available in Development). This lists `docugen.v1.ReportService` and `docugen.v1.ExportService`.
3. Under **Metadata**, add `authorization` with the value `Bearer <token>`.
4. Pick `ExportService/Export` and send:
   ```json
   { "report_key": "sales-orders", "format": "OUTPUT_FORMAT_XLSX", "parameters": { "FromDate": "2026-01-01", "ToDate": "2026-03-31" } }
   ```
5. Postman lists the progress messages, then the `file` message, then the chunks (as base64). That is good for checking the call; use a real client to save the file.

## Calling Docugen from another application

Every call needs a bearer token in the `authorization` metadata. For service-to-service calls with Entra ID, use the client credentials flow and give the calling app one of the report's roles.

Call `Export` and read the stream to the end: `progress` messages while the report runs, one `file` message, then `chunk` messages to append in order. To stop an export, cancel the call; Docugen stops the query. **Set a deadline** that fits the report: a large PDF can take a minute or more. For exports that take a while, make the call from a background job in your application and pass the progress on to your users.

.NET apps reference `src/Docugen.Contracts` (or a package built from it). Apps in other languages generate a client from `src/Docugen.Contracts/Protos/docugen/v1/docugen.proto`.

```csharp
using var channel = GrpcChannel.ForAddress("https://docugen.internal");
var exports = new ExportService.ExportServiceClient(channel);
var headers = new Metadata { { "authorization", $"Bearer {token}" } };

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

Insert a row into `docugen.Reports`. Copy one from `samples/sqlserver/sample-reports.sql` to start. Docugen loads it at the next refresh (within `Catalog:RefreshInterval`, 30 seconds by default). Then check that it passed validation:

```sql
INSERT INTO docugen.Reports (ReportKey, Name, Description, DataSource, Query, CountQuery, AllowedRoles, Parameters, Columns, FileName)
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
    N'Sales,Finance',
    N'[
      { "name": "FromDate", "label": "From", "type": "date", "required": true },
      { "name": "ToDate",   "label": "To",   "type": "date", "required": true }
    ]',
    N'[
      { "field": "Region" },
      { "field": "Orders",   "format": "#,##0" },
      { "field": "Quantity", "format": "#,##0" },
      { "field": "Total",    "format": "#,##0.00" }
    ]',
    N'sales-by-region-{FromDate:yyyyMMdd}-{ToDate:yyyyMMdd}');

-- Half a minute later:
SELECT * FROM docugen.ReportValidation WHERE ReportKey = N'sales-by-region';
```

To change a report, `UPDATE` its row. To retire one without deleting it, set `IsActive = 0`. `docugen.Reports` is a system-versioned table, so every earlier version is kept: `SELECT * FROM docugen.Reports FOR SYSTEM_TIME ALL WHERE ReportKey = N'sales-by-region'`.

### `docugen.Reports` columns

| Column | Required | Meaning |
|---|---|---|
| `ReportKey` | yes | What callers ask for: lower-case letters, digits and dashes. |
| `Name`, `Description` | `Name` | Shown by `ListReports` / `GetReport` and in PDFs. |
| `DataSource` | yes | A name under `Docugen:DataSources` in configuration. Connection strings stay in configuration, never in the table. |
| `CommandType` | | `Text` (default) or `StoredProcedure`. With `StoredProcedure`, `Query` holds the procedure name. |
| `Query` | yes | The SQL, with `@Parameter` placeholders. Every placeholder must be declared in `Parameters`. |
| `CountQuery` | | `SELECT COUNT(*) …` with the same filters. Gives exact progress, and over-limit exports fail before the main query runs. |
| `CommandTimeoutSeconds` | | Defaults to `Limits:DefaultCommandTimeoutSeconds` (60). |
| `AllowedRoles` | | Comma-separated roles, e.g. `Sales,Finance`. `NULL` lets any signed-in caller run the report. |
| `Formats` | | Comma-separated, default `pdf,xlsx,csv`. |
| `Parameters` | | JSON array; see below. |
| `Columns` | | JSON array of `{ "field", "header", "format", "width" }`: output order, headers, Excel number formats and widths. `NULL` exports every column the query returns, with its SQL name as the header. |
| `FileName` | | Download name without extension. Supports `{Param}`, `{Param:format}` and `{Now:format}`. |
| `Culture` | | Number and date formatting in PDFs, e.g. `id-ID`. Defaults to `Docugen:DefaultCulture`. |
| `PdfTemplate` | | Liquid/HTML template. `NULL` uses the built-in table layout. |
| `PdfPageSize`, `PdfLandscape`, `PdfMargin`, `PdfShowPageNumbers` | | Defaults `A4`, `0`, `15mm`, `1`. |
| `PdfMaxRows` | | Overrides `Limits:MaxPdfRows` for this report. |
| `IsActive` | | `0` hides the report. |

The table rejects a malformed `ReportKey` and invalid JSON when you insert. Everything else (the data source exists, every `@Param` is declared, defaults match their types, the template compiles, no unknown JSON properties such as a misspelt `"lable"`) is checked by Docugen and reported in `docugen.ReportValidation`.

### Parameter types

Each entry in `Parameters` is `{ "name", "type", "required", "label", "description", "default", "maxLength", "allowedValues" }`.

| Type | Value in the request | Sent to SQL Server as |
|---|---|---|
| `string` | `"text"` (checked against `maxLength` and `allowedValues`) | `nvarchar(4000)` |
| `int`, `long`, `decimal` | number, or a numeric string | `int`, `bigint`, `decimal` |
| `bool` | `true` / `false` | `bit` |
| `date` | `"2026-01-31"` | `date` |
| `datetime` | `"2026-01-31T13:45:00"` | `datetime2` |
| `int[]`, `string[]` | `[1, 2, 3]` | JSON string; read it with `OPENJSON(@Ids)` |

A parameter the caller leaves out is passed as `NULL` (or its `default`, if one is set). This makes optional filters straightforward:

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

Chromium runs with JavaScript disabled and **all network requests blocked**. Put images, fonts and CSS in `ReportAssets` rather than linking to URLs. Page numbers ("Page X of Y") are added in the footer unless `PdfShowPageNumbers` is `0`.

## gRPC API

Package `docugen.v1`; the full contract with comments is in [docugen.proto](src/Docugen.Contracts/Protos/docugen/v1/docugen.proto). Every call needs a bearer token except `grpc.health.v1.Health`.

| Service / method | Description |
|---|---|
| `ReportService/ListReports` | Reports the caller may run |
| `ReportService/GetReport` | Parameters, formats and columns of one report (the SQL is not exposed); `exact_progress` tells whether it has a count query |
| `ExportService/Export` | Runs a report. Streams `progress` messages, then one `file` message (name, content type, size, row count, export id), then 64 KiB `chunk`s |
| `grpc.health.v1.Health/Check` | Checks the store and every data source can be reached |

Progress is sent on every stage change and at most every quarter of a second while rows are written. The percentage is 0 while waiting for a slot, 2 while counting, 5 while querying, then 10 to 90 as rows are written (in proportion to `total_rows`), 90 while the file is completed (for PDFs, while Chromium prints) and 100 just before the file is sent. **Without a count query the total is unknown, so the percentage stays at 10 while rows are written;** `rows_written` still moves.

Errors before the export starts:

| Status code | When |
|---|---|
| `INVALID_ARGUMENT` | Missing or unsupported format, or unknown, missing or invalid parameters. The details carry a `google.rpc.BadRequest` with one violation per field (`parameters.FromDate`, `format`) |
| `UNAUTHENTICATED` / `PERMISSION_DENIED` | No valid token / the caller has none of the report's `AllowedRoles` |
| `NOT_FOUND` | Unknown report |
| `RESOURCE_EXHAUSTED` | Too many exports are running and waiting on this instance (`Limits:MaxConcurrentExports`, `Limits:MaxQueuedExports`); retry with backoff |

Errors while it runs carry a `google.rpc.ErrorInfo` with domain `docugen`, the reason below and `metadata["export_id"]`:

| Status code | Reason | Meaning |
|---|---|---|
| `FAILED_PRECONDITION` | `TOO_MANY_ROWS` | More rows than the format allows (PDF limit, Excel's 1,048,575 rows, or `Limits:MaxRows`) |
| `DEADLINE_EXCEEDED` | `QUERY_TIMEOUT` | The SQL query took longer than its command timeout |
| `INTERNAL` | `DEFINITION_MISMATCH` | `Columns` lists a field the query does not return |
| `INTERNAL` | `INTERNAL` | Anything else, e.g. PDF printing took longer than `Pdf:RenderTimeoutSeconds`; the full error is in the export log |

A call cancelled by the caller, or whose deadline passes, ends with the usual `CANCELLED` / `DEADLINE_EXCEEDED` and is logged as `Cancelled`.

## Tracking exports

Every `Export` call that gets a slot is recorded in the store, whether it succeeds or not:

- **`docugen.Exports`**: one row per export: report, format, parameter values, who asked (`RequestedBy`, `CallerId`), status (`Running`, `Succeeded`, `Failed`, `Cancelled`), rows, file size, error, start time and duration.
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
  "Authentication": {
    "Schemes": {
      "Bearer": {                                                 // e.g. Microsoft Entra ID
        "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ValidAudiences": ["api://docugen"]
      }
    }
  },
  "Docugen": {
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

The role check uses the token's role claims (`roles` or `role`). For Entra ID, define app roles such as `Sales` and `Finance` on the API's app registration and assign them to users, groups or calling applications.

## Security checklist

- **Whoever can write `docugen.Reports` can make Docugen run any SQL its data-source logins allow.** Grant `INSERT`/`UPDATE` on `Reports` and `ReportAssets` to report authors only, and review changes (the table's history shows who changed what, and when).
- **A report with no `AllowedRoles` can be run by any caller with a valid token for Docugen.** With many applications calling Docugen, give every report roles, and only assign roles to the applications that need them.
- Connect each data source with a **read-only SQL login** (`db_datareader`, or `EXECUTE` on specific procedures only). Add `ApplicationIntent=ReadOnly` to send queries to a readable secondary.
- Keep the store in its own database (or at least its own schema and login). The service's store login needs `SELECT` on `Reports` and `ReportAssets` and read/write on `ReportValidation`, `Exports` and `ExportEvents`; it should not be able to read the business data.
- Keep connection strings out of source control and out of the store: use environment variables, a key vault or `dotnet user-secrets`.
- Text in CSV files that starts with `=`, `+`, `-`, `@`, a tab or a carriage return gets an apostrophe in front (protection against CSV/formula injection). Numbers are left untouched. XLSX cells are typed, so the problem does not arise there.
- PDF templates cannot run JavaScript or reach the network, and data values are HTML-encoded.

## Deployment

```bash
docker build -t docugen .
docker run -p 8080:8080 \
  -e Authentication__Schemes__Bearer__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0 \
  -e Authentication__Schemes__Bearer__ValidAudiences__0=api://docugen \
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

- Per-application limits, so one busy caller cannot take every export slot.
- Metrics and tracing (OpenTelemetry): export duration, failures and slot waits per report and per caller.
- Publish `Docugen.Contracts` as a NuGet package for .NET callers.
- A preview call that returns the first N rows, for building report screens.
- Admin calls to create and validate reports, for authors who should not have direct table access.
