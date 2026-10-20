using Docugen.Core.Data;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Rendering.Pdf;
using UglyToad.PdfPig;

namespace Docugen.Infrastructure.Tests;

/// <summary>
/// Prints real PDFs with Chromium. Set DOCUGEN_BROWSER_PATH to use an existing Chromium binary, or install the
/// matching browser with "dotnet src/Docugen.Api/bin/.../Docugen.Api.dll install-browsers".
/// </summary>
public sealed class PdfRendererTests : IAsyncLifetime
{
    private static readonly ColumnInfo[] Columns =
    [
        new("Customer", "Customer", typeof(string)),
        new("OrderDate", "Order Date", typeof(DateTime)),
        new("Total", "Total", typeof(decimal)),
    ];

    private readonly ChromiumPdfConverter _converter = new(TestSupport.Options());

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _converter.DisposeAsync();

    [Fact]
    public async Task Stops_promptly_when_cancelled_at_any_moment_and_frees_its_render_slot()
    {
        var settings = new PdfPageSettings("A4", false, "15mm", null);
        var html = "<html><body>" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"<p>Row {i}</p>")) + "</body></html>";
        await _converter.ConvertAsync("<p>warm-up</p>", settings, TestContext.Current.CancellationToken);

        foreach (var delayMs in new[] { 1, 2, 3, 5, 8, 13 })
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancel.CancelAfter(delayMs);

            // Cancelling in the first milliseconds used to leave the conversion, and its render slot, waiting forever.
            var conversion = _converter.ConvertAsync(html, settings, cancel.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }

        var pdf = await _converter.ConvertAsync("<p>after</p>", settings, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
    }

    [Fact]
    public async Task Prints_the_built_in_template_with_data_filters_and_page_numbers()
    {
        var report = TestSupport.Report() with
        {
            Name = "Quarterly Orders",
            Parameters = [new ParameterDefinition { Name = "Region", Label = "Sales Region" }],
        };

        var text = await RenderTextAsync(
            report,
            [new BoundParameter("Region", ParameterType.String, "North")],
            ["Aurora Trading", new DateTime(2026, 3, 1), 1234.5m],
            ["Bintang Logistics", new DateTime(2026, 3, 2), 99m]);

        Assert.Contains("Quarterly Orders", text);
        Assert.Contains("Sales Region: North", text);
        Assert.Contains("Aurora Trading", text);
        Assert.Contains("1,234.50", text);
        Assert.Contains("3/1/2026", text);
        Assert.Contains("2 rows", text);
        Assert.Contains("Page 1 of 1", text);
    }

    [Fact]
    public async Task Escapes_data_so_it_cannot_inject_markup_or_script()
    {
        var text = await RenderTextAsync(TestSupport.Report(), [], ["<script>document.write('pwned')</script><b>bold</b>", null, null]);

        // The markup shows up as literal text, and the script never ran (it would have added a second "pwned").
        Assert.Contains("<script>", text);
        Assert.Contains("<b>bold</b>", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "pwned"));
    }

    [Fact]
    public async Task Blocks_network_requests_from_templates()
    {
        var report = TestSupport.Report() with
        {
            PdfTemplateSource = "<html><body><img src=\"http://example.invalid/tracker.png\"><p>Offline {{ row_count }}</p></body></html>",
            Pdf = new PdfOptions { ShowPageNumbers = false },
        };

        var text = await RenderTextAsync(report, [], ["x", null, null]);

        Assert.Contains("Offline 1", text);
    }

    [Fact]
    public async Task Custom_templates_get_raw_values_formatted_in_the_report_culture()
    {
        var report = TestSupport.Report() with
        {
            PdfTemplateSource = "{% for r in rows %}{{ r.Customer }}={{ r.Total | format_number: \"N2\" }}|{{ r.OrderDate | format_date: \"dd MMM yyyy\" }};{% endfor %}",
        };
        var renderer = CreateRenderer();
        var context = TestSupport.Context(report, culture: "id-ID");

        var html = await renderer.RenderHtmlAsync(context, Columns, [["Aurora", new DateTime(2026, 3, 1), 1234.5m]]);

        Assert.Equal("Aurora=1.234,50|01 Mar 2026;", html);
    }

    [Fact]
    public void Uses_the_report_row_limit_before_the_global_one()
    {
        var renderer = CreateRenderer();

        Assert.Equal(5000, renderer.GetRowLimit(TestSupport.Report()));
        Assert.Equal(50, renderer.GetRowLimit(TestSupport.Report() with { Pdf = new PdfOptions { MaxRows = 50 } }));
    }

    private PdfRenderer CreateRenderer() =>
        new(new LiquidTemplateEngine(), _converter, ReportAssets.Empty, TestSupport.Options());

    private async Task<string> RenderTextAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, params object?[][] rows)
    {
        using var output = new MemoryStream();
        await CreateRenderer().RenderAsync(TestSupport.Context(report, parameters), TestSupport.Data(Columns, rows), output, TestContext.Current.CancellationToken);

        var bytes = output.ToArray();
        Assert.Equal("%PDF"u8.ToArray(), bytes[..4]);
        using var pdf = PdfDocument.Open(bytes);
        return string.Join("\n", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }
}
