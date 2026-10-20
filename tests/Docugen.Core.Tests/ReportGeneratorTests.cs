using Docugen.Core.Data;
using Docugen.Core.Exports;
using Docugen.Core.Parameters;
using Docugen.Core.Rendering;
using Docugen.Core.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Docugen.Core.Tests;

public class ReportGeneratorTests
{
    private static readonly ColumnInfo[] Columns = [new("Id", "Id", typeof(int))];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Renders_into_the_output_stream_and_describes_the_file()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 3), new FakeRenderer(limit: null));
        using var output = new MemoryStream();

        var result = await generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], "alice", output, progress: null, Cancel);

        Assert.Equal(3, result.RowCount);
        Assert.Equal("text/plain", result.ContentType);
        Assert.EndsWith(".txt", result.FileName);
        Assert.Equal("1\n2\n3\n", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task Reports_stages_and_rows_in_order()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 3), new FakeRenderer(limit: null));
        var progress = new RecordingProgress();

        await generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, Stream.Null, progress, Cancel);

        Assert.Equal(["Querying", "WritingRows", "rows:1", "rows:2", "rows:3", "Finalizing"], progress.Calls);
    }

    [Fact]
    public async Task Runs_the_count_query_first_and_reports_the_total()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 2, count: 2), new FakeRenderer(limit: null));
        var progress = new RecordingProgress();
        var report = TestReports.Create() with { CountQuery = "SELECT COUNT(*) FROM T" };

        await generator.GenerateAsync(report, OutputFormat.Csv, [], null, Stream.Null, progress, Cancel);

        Assert.Equal(["CountingRows", "total:2", "Querying", "WritingRows"], progress.Calls.Take(4));
    }

    [Fact]
    public async Task Fails_before_the_main_query_when_the_count_is_over_the_limit()
    {
        var dataSource = new FakeDataSource(rows: 11, count: 11);
        var generator = CreateGenerator(dataSource, new FakeRenderer(limit: 10));
        var report = TestReports.Create() with { CountQuery = "SELECT COUNT(*) FROM T" };

        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            generator.GenerateAsync(report, OutputFormat.Csv, [], null, Stream.Null, null, Cancel));

        Assert.Equal(10, error.Limit);
        Assert.False(dataSource.Executed);
    }

    [Fact]
    public async Task Applies_the_renderer_row_limit()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 11), new FakeRenderer(limit: 10));

        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, Stream.Null, null, Cancel));

        Assert.Equal(10, error.Limit);
    }

    [Fact]
    public async Task Applies_the_global_row_limit_when_lower_than_the_renderer_limit()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 6), new FakeRenderer(limit: 100), maxRows: 5);

        var error = await Assert.ThrowsAsync<ReportLimitExceededException>(() =>
            generator.GenerateAsync(TestReports.Create(), OutputFormat.Csv, [], null, Stream.Null, null, Cancel));

        Assert.Equal(5, error.Limit);
    }

    [Fact]
    public async Task Rejects_formats_the_report_does_not_offer()
    {
        var generator = CreateGenerator(new FakeDataSource(rows: 1), new FakeRenderer(limit: null));
        var report = TestReports.Create() with { Formats = [OutputFormat.Pdf] };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            generator.GenerateAsync(report, OutputFormat.Csv, [], null, Stream.Null, null, Cancel));
    }

    private static ReportGenerator CreateGenerator(IReportDataSource dataSource, IDocumentRenderer renderer, long maxRows = 1000) =>
        new(
            dataSource,
            [renderer],
            Options.Create(new DocugenOptions { Limits = new LimitOptions { MaxRows = maxRows } }),
            TimeProvider.System,
            NullLogger<ReportGenerator>.Instance);

    private sealed class FakeDataSource(int rows, long count = 0) : IReportDataSource
    {
        public bool Executed { get; private set; }

        public Task<ReportData> ExecuteAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken)
        {
            Executed = true;
            return Task.FromResult<ReportData>(new InMemoryReportData(Columns, Enumerable.Range(1, rows).Select(i => new object?[] { i })));
        }

        public Task<long> CountAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken) =>
            Task.FromResult(count);
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

    private sealed class RecordingProgress : IReportProgress
    {
        public List<string> Calls { get; } = [];

        public void StageChanged(ExportStage stage) => Calls.Add(stage.ToString());

        public void TotalRowsCounted(long totalRows) => Calls.Add($"total:{totalRows}");

        public void RowsWritten(long rows) => Calls.Add($"rows:{rows}");
    }
}
