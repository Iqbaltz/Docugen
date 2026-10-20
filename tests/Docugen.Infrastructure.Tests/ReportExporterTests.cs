using System.Runtime.CompilerServices;
using Docugen.Core;
using Docugen.Core.Data;
using Docugen.Core.Exports;
using Docugen.Core.Parameters;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Exports;
using Docugen.Infrastructure.Rendering;
using Docugen.Infrastructure.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Docugen.Infrastructure.Tests;

public sealed class ReportExporterTests
{
    private static readonly ExportCaller Alice = new("alice-id", "alice@example.com");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hands_over_the_finished_file_and_logs_every_step()
    {
        using var exporter = Exporter.Create(new GatedDataSource(rows: 5));
        var progress = new ExportProgressState();
        string? content = null;
        string? tempFile = null;

        var record = await exporter.RunAsync(progress, async (file, token) =>
        {
            Assert.Equal(ExportStage.Completed, progress.Read().Stage);
            tempFile = ((FileStream)file.Content).Name;
            content = await new StreamReader(file.Content).ReadToEndAsync(token);
        });

        Assert.Equal((ExportStatus.Succeeded, 5L), (record.Status, record.RowsExported));
        Assert.Matches(@"^test-report-\d{8}-\d{6}\.csv$", record.FileName);
        Assert.Equal(6, content!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.False(File.Exists(tempFile));
        Assert.Equal(100, progress.Read().Percent);

        var stored = await exporter.Log.FindAsync(record.Id, Cancel);
        Assert.Equal((ExportStatus.Succeeded, "alice@example.com", 5L), (stored!.Status, stored.RequestedBy, stored.RowsExported));
        Assert.NotNull(stored.DurationMs);
        Assert.Equal(
            ["Started", "Querying", "WritingRows", "Finalizing", "Generated", "Sent"],
            (await exporter.Log.GetEventsAsync(record.Id, Cancel)).Select(e => e.EventType));
    }

    [Fact]
    public async Task Reports_exact_progress_when_the_report_has_a_count_query()
    {
        var data = new GatedDataSource(rows: 100, pauseAfter: 50);
        using var exporter = Exporter.Create(data, report: r => r with { CountQuery = "SELECT 100" });
        var progress = new ExportProgressState();

        var running = exporter.RunAsync(progress);
        await data.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        var halfway = progress.Read();
        data.Gate.SetResult();
        var record = await running;

        Assert.Equal(new ExportProgressSnapshot(ExportStage.WritingRows, 50, 50, 100), halfway);
        Assert.Equal(100L, record.TotalRows);
    }

    [Fact]
    public async Task Without_a_count_query_progress_stays_at_ten_while_rows_move()
    {
        var data = new GatedDataSource(rows: 100, pauseAfter: 50);
        using var exporter = Exporter.Create(data);
        var progress = new ExportProgressState();

        var running = exporter.RunAsync(progress);
        await data.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        var halfway = progress.Read();
        data.Gate.SetResult();
        await running;

        Assert.Equal(new ExportProgressSnapshot(ExportStage.WritingRows, 10, 50, null), halfway);
    }

    [Fact]
    public async Task Stops_and_logs_a_cancellation_when_the_caller_cancels()
    {
        var data = new GatedDataSource(rows: 10, pauseAfter: 2);
        using var exporter = Exporter.Create(data);
        using var caller = new CancellationTokenSource();

        var running = exporter.RunCancellableAsync(new ExportProgressState(), null, caller.Token);
        await data.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var record = await exporter.LastAsync();
        Assert.Equal((ExportStatus.Cancelled, 2L), (record.Status, record.RowsExported));
        Assert.Contains((await exporter.Log.GetEventsAsync(record.Id, Cancel)).Select(e => e.EventType), t => t == "Cancelled");
    }

    [Fact]
    public async Task Logs_a_cancellation_when_the_caller_disconnects_while_the_file_is_sent()
    {
        using var exporter = Exporter.Create(new GatedDataSource(rows: 3));
        using var caller = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.RunCancellableAsync(
            new ExportProgressState(),
            async (_, token) =>
            {
                await caller.CancelAsync();
                throw new IOException("The client disconnected.");
            },
            caller.Token));

        Assert.Equal(ExportStatus.Cancelled, (await exporter.LastAsync()).Status);
    }

    [Fact]
    public async Task Rejects_new_exports_when_every_slot_and_queue_place_is_taken()
    {
        var data = new GatedDataSource(rows: 3, pauseAfter: 1);
        using var exporter = Exporter.Create(data, o => o.Limits = new LimitOptions { MaxConcurrentExports = 1, MaxQueuedExports = 1 });

        var first = exporter.RunAsync(new ExportProgressState());
        await data.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        var waitingProgress = new ExportProgressState();
        var waiting = exporter.RunAsync(waitingProgress);

        await Assert.ThrowsAsync<ExportBusyException>(() => exporter.RunAsync(new ExportProgressState()));
        Assert.Equal(ExportStage.Queued, waitingProgress.Read().Stage);

        data.Gate.SetResult();
        Assert.Equal(ExportStatus.Succeeded, (await first).Status);
        Assert.Equal(ExportStatus.Succeeded, (await waiting).Status);
    }

    [Fact]
    public async Task Reports_failures_with_a_code_and_keeps_the_details_in_the_log()
    {
        var data = new GatedDataSource(rows: 1) { Failure = new ReportQueryTimeoutException("test-report", new TimeoutException("Execution Timeout Expired.")) };
        using var exporter = Exporter.Create(data);

        var error = await Assert.ThrowsAsync<ExportFailedException>(() => exporter.RunAsync(new ExportProgressState()));

        Assert.Equal(ExportErrorCodes.QueryTimeout, error.Code);
        Assert.Contains("took too long", error.Message);
        var record = await exporter.Log.FindAsync(error.ExportId, Cancel);
        Assert.Equal((ExportStatus.Failed, ExportErrorCodes.QueryTimeout), (record!.Status, record.ErrorCode));
        var failed = (await exporter.Log.GetEventsAsync(error.ExportId, Cancel)).Single(e => e.EventType == "Failed");
        Assert.Contains("ReportQueryTimeoutException", failed.Message);
    }

    [Fact]
    public async Task Keeps_exporting_when_the_export_log_is_unavailable()
    {
        using var exporter = Exporter.Create(new GatedDataSource(rows: 2), log: new BrokenExportLog());

        var record = await exporter.RunAsync(new ExportProgressState());

        Assert.Equal(ExportStatus.Succeeded, record.Status);
    }

    [Theory]
    [InlineData(ExportStage.Queued, 0, null, 0)]
    [InlineData(ExportStage.Querying, 0, null, 5)]
    [InlineData(ExportStage.WritingRows, 500, null, 10)]
    [InlineData(ExportStage.WritingRows, 0, 200L, 10)]
    [InlineData(ExportStage.WritingRows, 100, 200L, 50)]
    [InlineData(ExportStage.WritingRows, 300, 200L, 90)]
    [InlineData(ExportStage.Finalizing, 200, 200L, 90)]
    [InlineData(ExportStage.Completed, 200, 200L, 100)]
    public void Maps_stages_and_rows_to_a_percentage(ExportStage stage, long rows, long? total, int expected) =>
        Assert.Equal(expected, ExportProgressState.Percent(stage, rows, total));

    /// <summary>A real exporter over a Sqlite export log, with a data source the test controls.</summary>
    private sealed class Exporter : IDisposable
    {
        private readonly ReportExporter _exporter;
        private readonly ReportDefinition _report;
        private readonly string _store;

        private Exporter(ReportExporter exporter, ReportDefinition report, IExportLog log, string store)
        {
            _exporter = exporter;
            _report = report;
            _store = store;
            Log = log;
        }

        public IExportLog Log { get; }

        public static Exporter Create(
            IReportDataSource data,
            Action<DocugenOptions>? configure = null,
            Func<ReportDefinition, ReportDefinition>? report = null,
            IExportLog? log = null)
        {
            var options = TestSupport.StoreOptions(o =>
            {
                o.TempPath = Directory.CreateTempSubdirectory("docugen-temp-").FullName;
                configure?.Invoke(o);
            });
            log ??= new SqlExportLog(new StoreDatabase(options, new TestHostEnvironment(TestSupport.RepoRoot)));
            var generator = new ReportGenerator(data, [new CsvRenderer(options)], options, TimeProvider.System, NullLogger<ReportGenerator>.Instance);
            var definition = TestSupport.Report() with { Formats = [OutputFormat.Csv] };
            var exporter = new ReportExporter(generator, log, options, TimeProvider.System, NullLoggerFactory.Instance);
            return new Exporter(exporter, report?.Invoke(definition) ?? definition, log, options.Value.Store.ConnectionString);
        }

        public Task<ExportRecord> RunAsync(IReportProgress progress, Func<ExportedFile, CancellationToken, Task>? deliver = null) =>
            RunCancellableAsync(progress, deliver, Cancel);

        public Task<ExportRecord> RunCancellableAsync(IReportProgress progress, Func<ExportedFile, CancellationToken, Task>? deliver, CancellationToken cancellationToken) =>
            _exporter.ExportAsync(
                _report,
                OutputFormat.Csv,
                [],
                Alice,
                progress,
                deliver ?? ((file, token) => file.Content.CopyToAsync(Stream.Null, token)),
                cancellationToken);

        /// <summary>The newest export in the log, for exports that ended in an exception without an id.</summary>
        public async Task<ExportRecord> LastAsync()
        {
            var rows = await TestSupport.QueryAsync(_store, "SELECT ExportId FROM Exports ORDER BY StartedAtUtc DESC LIMIT 1");
            return (await Log.FindAsync(Guid.Parse((string)rows.Single()["ExportId"]!), Cancel))!;
        }

        public void Dispose() => _exporter.Dispose();
    }

    private sealed class BrokenExportLog : IExportLog
    {
        public Task CreateAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The store is down.");

        public Task SaveAsync(ExportRecord record, IReadOnlyList<ExportEvent> events, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The store is down.");

        public Task<ExportRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ExportRecord?>(null);

        public Task<IReadOnlyList<ExportEvent>> GetEventsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ExportEvent>>([]);
    }

    /// <summary>Yields numbered rows; can pause after a number of rows until <see cref="Gate"/> opens, or fail.</summary>
    private sealed class GatedDataSource(int rows, int pauseAfter = int.MaxValue) : IReportDataSource
    {
        private static readonly ColumnInfo[] IdColumn = [new("Id", "Id", typeof(int))];

        public int Rows { get; } = rows;

        public int PauseAfter { get; } = pauseAfter;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Failure { get; init; }

        public Task<ReportData> ExecuteAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken) =>
            Failure is not null ? Task.FromException<ReportData>(Failure) : Task.FromResult<ReportData>(new GatedData(this));

        public Task<long> CountAsync(ReportDefinition report, IReadOnlyList<BoundParameter> parameters, CancellationToken cancellationToken) =>
            Task.FromResult((long)Rows);

        private sealed class GatedData(GatedDataSource source) : ReportData(IdColumn)
        {
            protected override async IAsyncEnumerable<object?[]> ReadRowsCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
            {
                for (var i = 1; i <= source.Rows; i++)
                {
                    if (i == source.PauseAfter + 1)
                    {
                        source.Paused.TrySetResult();
                        await source.Gate.Task.WaitAsync(cancellationToken);
                    }

                    yield return [i];
                }
            }
        }
    }
}
