# Docugen

Docugen is an internal service that turns **named SQL Server queries** into **PDF, Excel (XLSX) and CSV** documents. It is built on .NET 10 and ASP.NET Core minimal APIs.

Developers write each report once: the SQL, the parameters callers may pass, and optionally an HTML template for the PDF. The report is reviewed in Git like any other code. Callers then ask for a report by name, choose a format and pass parameter values. They never send SQL.

```
POST /api/reports/sales-orders/render
{ "format": "pdf", "parameters": { "FromDate": "2026-01-01", "ToDate": "2026-03-31" } }
→ 200 application/pdf  (sales-orders-20260101-20260331.pdf)
```

## How it works

```
request ──► catalog ──► access check ──► parameter binding ──► SQL Server ──► renderer ──► temp file ──► download
            reports/     allowedRoles      typed values,          streamed      CSV  (CsvHelper)
            *.json       vs. JWT roles     400 on bad input       rows          XLSX (Open XML SDK, streaming)
                                                                                PDF  (Liquid template → Chromium)
```

| Project | Responsibility |
|---|---|
| `src/Docugen.Core` | Report model, parameter binding, the `ReportGenerator` that coordinates each request, renderer and data-source interfaces. Uses only Microsoft packages. |
| `src/Docugen.Infrastructure` | Loads and validates report definitions; ADO.NET data access (SQL Server, plus Sqlite for demos); the CSV, XLSX and PDF renderers. |
| `src/Docugen.Api` | HTTP endpoints, JWT authentication, ProblemDetails errors, rate limiting, OpenAPI, health checks. |
| `reports/` | Report definitions, one folder per report, plus shared assets in `_assets/`. |
| `samples/` | Demo database scripts for Sqlite (local runs) and SQL Server (integration tests). |

Design choices:

- **No SQL from callers.** Values are always sent to the database as typed `DbParameter`s, never concatenated into the SQL text.
- **Streaming.** Rows go from the data reader straight into the CSV and XLSX writers, so memory stays flat even for hundreds of thousands of rows. PDF output is capped by default at 5,000 rows, since a PDF that long is unreadable anyway.
- **Each document is rendered to a temporary file first, then sent.** A failure partway through therefore returns a proper error response rather than a truncated download. The temporary file is deleted as soon as the response has been sent.
- **Fail fast.** Every report definition is checked at startup, and the API refuses to start if any check fails. The checks: the JSON is valid, the data source exists, every `@Param` used in the SQL is declared, and the template compiles.
- **Free licenses only.** Every dependency is MIT, Apache or BSD licensed and free for commercial use. EPPlus, iText and QuestPDF are deliberately not used.

## Quick start (no SQL Server needed)

In Development, the `SalesDb` data source points at a Sqlite demo database. It is created from `samples/sqlite/seed.sql` the first time the API starts.

```bash
# 1. Install Chromium for PDF rendering (one time)
dotnet build
dotnet src/Docugen.Api/bin/Debug/net10.0/Docugen.Api.dll install-browsers

# 2. Create a development token (the user must be in role Sales or Finance to run sales-orders)
cd src/Docugen.Api
dotnet user-jwts create --name alice@example.com --role Sales --output token

# 3. Run the API (http://localhost:5180, API explorer at /scalar)
dotnet run

# 4. Download a report
curl -X POST http://localhost:5180/api/reports/sales-orders/render \
  -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"format":"xlsx","parameters":{"FromDate":"2026-01-01","ToDate":"2026-03-31"}}' \
  -o sales-orders.xlsx
```

If a Chromium build is already installed somewhere else, you can skip step 1. Point Docugen at it with `Docugen:Pdf:BrowserExecutablePath` instead.

## Adding a report

Create a folder under `reports/` whose name is the report key:

```
reports/
  sales-orders/
    report.json      required: the definition
    query.sql        the SQL (or put "query" inline in report.json)
    template.html    optional PDF template (Liquid); without it the built-in table layout is used
  _assets/           shared logo, CSS and fonts for templates
```

### `report.json`

```jsonc
{
  "key": "sales-orders",                  // must match the folder name: lower-case letters, digits, dashes
  "name": "Sales Orders",
  "description": "Orders placed in the selected period.",
  "dataSource": "SalesDb",                // a name under Docugen:DataSources in configuration
  "commandType": "Text",                  // or "StoredProcedure" (query.sql then holds the procedure name)
  "commandTimeoutSeconds": 60,
  "allowedRoles": ["Sales", "Finance"],   // empty or omitted = any signed-in user
  "formats": ["pdf", "xlsx", "csv"],
  "culture": "en-US",                     // number and date formatting in PDFs, e.g. "id-ID"
  "parameters": [
    { "name": "FromDate", "label": "From", "type": "date", "required": true },
    { "name": "Region", "type": "string", "allowedValues": ["North", "South", "East", "West"] },
    { "name": "Top", "type": "int", "default": 100 }
  ],
  "columns": [                            // optional: output order, headers, Excel formats and widths
    { "field": "OrderDate", "header": "Order Date", "format": "dd-mmm-yyyy", "width": 14 },
    { "field": "Total", "header": "Total", "format": "#,##0.00" }
  ],
  "fileName": "sales-orders-{FromDate:yyyyMMdd}-{ToDate:yyyyMMdd}",   // {Param}, {Param:format}, {Now:format}
  "pdf": { "template": "template.html", "pageSize": "A4", "landscape": false, "margin": "15mm", "maxRows": 5000 }
}
```

If `columns` is omitted, every column the query returns is exported, using its SQL column name as the header. Unknown properties are rejected, so a typo such as `"formts"` stops startup instead of being silently ignored.

### Parameter types

| Type | JSON value | Sent to SQL Server as |
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

## PDF templates

Templates use [Liquid](https://shopify.github.io/liquid/) syntax, rendered by [Fluid](https://github.com/sebastienros/fluid), and are printed by headless Chromium. That means ordinary HTML and CSS work, including `@page`, `break-before: page` and table headers that repeat on every page. **Every `{{ value }}` is HTML-encoded automatically.** Use `{{ value | raw }}` only for trusted content such as the shared CSS.

| Variable | Contents |
|---|---|
| `report.key`, `report.name`, `report.description` | From `report.json` |
| `parameters.<Name>` | Bound parameter values (dates as dates, `nil` when omitted) |
| `parameter_list` | `[{ name, label, display }]`, convenient for printing the active filters |
| `columns` | `[{ field, header, kind, numeric, format }]`; `kind` is one of `integer`, `number`, `date`, `datetime`, `bool`, `text` |
| `rows` | One object per row, keyed by field: `{{ r.Total }}` |
| `display_rows` | The same rows as lists of preformatted strings (what the built-in template uses) |
| `row_count`, `generated_at`, `generated_at_display`, `generated_by` | Run information |
| `assets.<file_name>` | Files from `reports/_assets/`, with dots and dashes turned into underscores. Images and fonts are data URIs (`assets.logo_svg`); CSS is text (`assets.report_css`) |

Useful filters: `format_number: "N2"` and `format_date: "dd MMM yyyy"`, which both use .NET format strings and the report's culture. Also `plus` for running totals, and `default`. See `reports/sales-orders/template.html` for a complete example with summary cards and a totals row.

Chromium runs with JavaScript disabled and **all network requests blocked**. Put images, fonts and CSS in `_assets/` rather than linking to URLs. Page numbers ("Page X of Y") are added in the footer unless `pdf.showPageNumbers` is `false`.

## API

All endpoints except `/health` require a bearer token.

| Method & path | Description |
|---|---|
| `GET /api/reports` | Reports the caller may run |
| `GET /api/reports/{key}` | Parameters, formats and columns of one report (the SQL is not exposed) |
| `POST /api/reports/{key}/render` | Body `{ "format": "pdf" \| "xlsx" \| "csv", "parameters": { ... } }`. Returns the file, with an `X-Row-Count` header |
| `GET /health` | Checks that every configured data source can be reached |
| `GET /openapi/v1.json`, `/scalar` | OpenAPI document and interactive explorer (Development only) |

Errors are returned as [ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) JSON:

| Status | When |
|---|---|
| 400 | Unknown, missing or invalid parameters, with an error per field; unsupported format |
| 401 / 403 | No valid token / the user has none of the report's `allowedRoles` |
| 404 | Unknown report |
| 422 | More rows than the format allows (PDF limit, Excel's 1,048,575 rows, or `Limits:MaxRows`) |
| 429 | Too many renders running and queued |
| 504 | The SQL query timed out |

Every render writes an `AUDIT` log entry with the user, report, format, parameter values and row count.

## Configuration

Settings live in `appsettings.json` and can be overridden by environment variables (use `__` for nesting, e.g. `Docugen__DataSources__SalesDb__ConnectionString`).

```jsonc
{
  "Authentication": {
    "Schemes": {
      "Bearer": {                                  // e.g. Microsoft Entra ID
        "Authority": "https://login.microsoftonline.com/<tenant-id>/v2.0",
        "ValidAudiences": ["api://docugen"]
      }
    }
  },
  "Docugen": {
    "ReportsPath": "reports",
    "DefaultCulture": "en-US",
    "DataSources": {
      "SalesDb": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=sql01;Database=Sales;Authentication=Active Directory Default;Encrypt=True;ApplicationIntent=ReadOnly"
      }
    },
    "Limits": { "MaxRows": 500000, "MaxPdfRows": 5000, "DefaultCommandTimeoutSeconds": 60, "MaxConcurrentRenders": 8, "MaxQueuedRenders": 50 },
    "Csv": { "Delimiter": ",", "IncludeBom": true, "EscapeFormulas": true, "Culture": "" },
    "Pdf": { "BrowserExecutablePath": null, "MaxConcurrentRenders": 4, "RenderTimeoutSeconds": 120 }
  }
}
```

The role check uses the token's role claims (`roles` or `role`). For Entra ID, define app roles such as `Sales` and `Finance` on the API's app registration and assign them to users or groups.

## Security checklist

- Connect with a **read-only SQL login** (`db_datareader`, or `EXECUTE` on specific procedures only). Add `ApplicationIntent=ReadOnly` to send queries to a readable secondary.
- Keep connection strings out of source control: use environment variables, a key vault or `dotnet user-secrets`.
- Report SQL is code. Review changes under `reports/` like any other pull request.
- Text in CSV files that starts with `=`, `+`, `-`, `@`, a tab or a carriage return gets an apostrophe in front (protection against CSV/formula injection). Numbers are left untouched. XLSX cells are typed, so the problem does not arise there.
- PDF templates cannot run JavaScript or reach the network, and data values are HTML-encoded.

## Deployment

```bash
docker build -t docugen .
docker run -p 8080:8080 \
  -e Authentication__Schemes__Bearer__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0 \
  -e Authentication__Schemes__Bearer__ValidAudiences__0=api://docugen \
  -e Docugen__DataSources__SalesDb__ConnectionString="Server=...;Database=Sales;..." \
  docugen
```

The image is built on `mcr.microsoft.com/dotnet/aspnet:10.0` and adds a Chromium build that matches the Playwright package, plus fonts. Report definitions are copied in at `/app/reports`. To change reports without rebuilding the image, mount a volume over that folder; the app reads the definitions at startup, so restart it after editing them.

To deploy without Docker (on Windows or Linux), run `dotnet publish src/Docugen.Api -c Release`. Then run `Docugen.Api install-browsers` once on the server and copy `reports/` next to the app.

## Development

```bash
dotnet build
dotnet test        # unit, renderer, API and (when Docker is running) SQL Server integration tests
```

- PDF tests print real documents. Set `DOCUGEN_BROWSER_PATH` to use an existing Chromium binary, or run `install-browsers` as shown above.
- `SqlServerIntegrationTests` starts SQL Server 2022 with Testcontainers, seeds it from `samples/sqlserver/seed.sql`, and checks that the sample report returns the same rows as the Sqlite demo for every format. The tests skip themselves when Docker is not available.
- CI (`.github/workflows/ci.yml`) builds the solution, installs Chromium, runs every test and builds the Docker image.

## Roadmap

- **Background jobs for large exports:** `POST /api/reports/{key}/jobs` returns 202 with a job id; then `GET /api/jobs/{id}` for status and `GET /api/jobs/{id}/file` to download. Jobs run on a `Channel<T>` + `BackgroundService`, reusing `ReportGenerator` unchanged. Results go to local disk or blob storage and are cleaned up after a TTL.
- A JSON preview endpoint (first N rows) for building report screens.
- Report definitions stored in a database, behind the existing `IReportCatalog` interface, if non-developers need to manage them.
