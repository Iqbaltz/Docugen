-- Sample reports and template assets for the Docugen store (SQL Server). The reports read the demo sales
-- database (samples/sqlserver/seed.sql) through the SalesDb data source. Safe to re-run: the samples are replaced.
--   sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d Docugen -i samples/sqlserver/sample-reports.sql
-- Copy these as the starting point for a new report. Columns left out take their defaults; see
-- db/sqlserver/docugen-store.sql.

-- Deleting a report also deletes its parameters, columns and PDF settings.
DELETE FROM docugen.Reports WHERE ReportKey IN (N'customer-list', N'sales-orders');
DELETE FROM docugen.ReportAssets WHERE FileName IN (N'logo.svg', N'report.css');

-- Customer List: every column the query returns, the built-in PDF layout, no parameters.
INSERT INTO docugen.Reports (ReportKey, Name, Description, DataSource, Query, FileName)
VALUES (
    N'customer-list',
    N'Customer List',
    N'All customers with their region and contact email.',
    N'SalesDb',
    N'SELECT
    c.Name AS [Customer],
    c.City,
    r.Name AS [Region],
    c.Email,
    c.CreatedOn AS [Customer Since]
FROM Customers c
JOIN Regions r ON r.Id = c.RegionId
ORDER BY c.Name',
    N'customers-{Now:yyyyMMdd}');

-- Sales Orders: parameters, chosen columns, a count query for exact progress, and a PDF template.
INSERT INTO docugen.Reports (ReportKey, Name, Description, DataSource, Query, CountQuery, CommandTimeoutSeconds, FileName)
VALUES (
    N'sales-orders',
    N'Sales Orders',
    N'Orders placed in the selected period, with totals.',
    N'SalesDb',
    N'SELECT
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
    N'SELECT COUNT(*)
FROM Orders o
JOIN Customers c ON c.Id = o.CustomerId
JOIN Regions r ON r.Id = c.RegionId
WHERE o.OrderDate >= @FromDate
  AND o.OrderDate <= @ToDate
  AND (@Region IS NULL OR r.Name = @Region)',
    60,
    N'sales-orders-{FromDate:yyyyMMdd}-{ToDate:yyyyMMdd}');

INSERT INTO docugen.ReportParameters (ReportKey, Name, SortOrder, Type, IsRequired, Label, AllowedValues) VALUES
    (N'sales-orders', N'FromDate', 1, N'date',   1, N'From',   NULL),
    (N'sales-orders', N'ToDate',   2, N'date',   1, N'To',     NULL),
    (N'sales-orders', N'Region',   3, N'string', 0, N'Region', N'North,South,East,West');

INSERT INTO docugen.ReportColumns (ReportKey, Field, SortOrder, Header, Format, Width) VALUES
    (N'sales-orders', N'OrderDate',   1, N'Order Date', N'dd-mmm-yyyy', 14),
    (N'sales-orders', N'OrderNumber', 2, N'Order No.',  NULL,           NULL),
    (N'sales-orders', N'Customer',    3, N'Customer',   NULL,           28),
    (N'sales-orders', N'Region',      4, N'Region',     NULL,           NULL),
    (N'sales-orders', N'Quantity',    5, N'Qty',        N'#,##0',       NULL),
    (N'sales-orders', N'Total',       6, N'Total',      N'#,##0.00',    14);

INSERT INTO docugen.ReportPdfSettings (ReportKey, MaxRows, Culture, Template)
VALUES (
    N'sales-orders',
    5000,
    N'en-US',
    N'<!DOCTYPE html>
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
');

-- Shared template assets: assets.logo_svg and assets.report_css in templates.
INSERT INTO docugen.ReportAssets (FileName, TextContent) VALUES (N'logo.svg', N'<svg xmlns="http://www.w3.org/2000/svg" width="200" height="40" viewBox="0 0 200 40">
  <rect x="0" y="4" width="32" height="32" rx="7" fill="#1f4e79"/>
  <text x="16" y="25" text-anchor="middle" font-family="Arial, sans-serif" font-size="14" font-weight="700" fill="#ffffff">TB</text>
  <text x="42" y="27" font-family="Arial, sans-serif" font-size="18" font-weight="700" fill="#1f4e79">TBIG</text>
</svg>');

INSERT INTO docugen.ReportAssets (FileName, TextContent) VALUES (N'report.css', N'* { box-sizing: border-box; }
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
