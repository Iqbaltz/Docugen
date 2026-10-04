using Docugen.Core.Data;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Docugen.Core.Tests;

public class ReportGeneratorTests
{
    private static readonly ColumnInfo[] Columns = [new("Id", "Id", typeof(int))];

    [Fact]
    public async Task Renders_to_a_temp_file_and_returns_it_positioned_at_the_start()
    {
        var generator = CreateGenerator(rows: 3, new FakeRenderer(limit: null));

        await using var document = await generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], "alice", TestContext.Current.CancellationToken);

        Assert.Equal(3, document.RowCount);
        Assert.Equal("text/plain", document.ContentType);
        Assert.EndsWith(".txt", document.FileName);
        using var reader = new StreamReader(document.Content);
        Assert.Equal("1\n2\n3\n", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deletes_the_temp_file_when_the_document_is_disposed()
    {
        var generator = CreateGenerator(rows: 1, new FakeRenderer(limit: null));

        var document = await generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, TestContext.Current.CancellationToken);
        var path = ((FileStream)document.Content).Name;
        Assert.True(File.Exists(path));

        await document.DisposeAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Applies_the_renderer_row_limit()
    {
        var generator = CreateGenerator(rows: 11, new FakeRenderer(limit: 10));

        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, TestContext.Current.CancellationToken));

        Assert.Equal(10, error.Limit);
    }

    [Fact]
    public async Task Applies_the_global_row_limit_when_lower_than_the_renderer_limit()
    {
        var generator = CreateGenerator(rows: 6, new FakeRenderer(limit: 100), maxRows: 5);

        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, TestContext.Current.CancellationToken));

        Assert.Equal(5, error.Limit);
    }

    [Fact]
    public async Task Rejects_formats_the_report_does_not_offer()
    {
        var generator = CreateGenerator(rows: 1, new FakeRenderer(limit: null));
        var report = TestReports.Create() with { Formats = [OutputFormat.Pdf] };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            generator.GenerateAsync(report, OutputFormat.Csv, [], null, TestContext.Current.CancellationToken));
    }

    private static ReportGenerator CreateGenerator(int rows, IDocumentRenderer renderer, long maxRows = 1000) =>
        new(
            new FakeDataSource(rows),
            [renderer],
            Options.Create(new DocugenOptions { Limits = new LimitOptions { MaxRows = maxRows } }),
            TimeProvider.System,
            NullLogger<ReportGenerator>.Instance);

    private sealed class FakeDataSource(int rows) : IReportDataSource
    {
        public Task<ReportData> ExecuteAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken) =>
            Task.FromResult<ReportData>(new InMemoryReportData(Columns, Enumerable.Range(1, rows).Select(i => new object?[] { i })));
    }

    private sealed class FakeRenderer(long? limit) : IDocumentRenderer
    {
        public OutputFormat Format => OutputFormat.Csv;

        public string ContentType => "text/plain";

        public string FileExtension => ".txt";

        public long? GetRowLimit(ReportDefinition report) => limit;

        public async Task RenderAsync(RenderContext context, ReportData data, Stream output, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(output, leaveOpen: true);
            await foreach (var row in data.ReadRowsAsync(cancellationToken))
            {
                await writer.WriteAsync($"{row[0]}\n");
            }
        }
    }
}
