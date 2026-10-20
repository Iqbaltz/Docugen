using System.Buffers;
using Docugen.Contracts.V1;
using Docugen.Core.Parameters;
using Docugen.Infrastructure.Exports;
using Google.Protobuf;
using Google.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using IReportCatalog = Docugen.Core.Reports.IReportCatalog;

namespace Docugen.Api.Services;

[Authorize]
public sealed class ExportGrpcService(IReportCatalog catalog, ReportExporter exporter) : ExportService.ExportServiceBase
{
    private const int ChunkSize = 64 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public override async Task Export(ExportRequest request, IServerStreamWriter<ExportResponse> responseStream, ServerCallContext context)
    {
        var user = context.GetHttpContext().User;
        var report = GrpcErrors.FindReport(catalog, request.ReportKey);

        var violations = new List<BadRequest.Types.FieldViolation>();
        var format = GrpcMapping.FromProto(request.Format);
        if (format is null || !report.Supports(format.Value))
        {
            violations.Add(new() { Field = "format", Description = $"Choose one of: {string.Join(", ", report.Formats.Select(GrpcMapping.Name))}." });
        }

        var binding = ParameterBinder.Bind(report, GrpcMapping.ToJson(request.Parameters));
        violations.AddRange(binding.Errors.SelectMany(e => e.Value.Select(message => new BadRequest.Types.FieldViolation { Field = $"parameters.{e.Key}", Description = message })));
        if (violations.Count > 0)
        {
            throw GrpcErrors.InvalidArgument(violations);
        }

        var cancellationToken = context.CancellationToken;
        var progress = new ExportProgressState();
        var sendingProgress = SendProgressAsync(progress, responseStream, cancellationToken);
        try
        {
            await exporter.ExportAsync(
                report,
                format!.Value,
                binding.Parameters,
                CallerIdentity.From(user),
                progress,
                async (file, token) =>
                {
                    // The exporter reported Completed, so the progress loop sends 100% and ends; only then is it safe
                    // to write the file to the same stream.
                    await sendingProgress;
                    await responseStream.WriteAsync(new ExportResponse { File = GrpcMapping.ToProto(file) }, token);
                    await SendFileAsync(file.Content, responseStream, token);
                },
                cancellationToken);
        }
        catch (ExportBusyException ex)
        {
            throw GrpcErrors.Busy(ex);
        }
        catch (ExportFailedException ex)
        {
            throw GrpcErrors.Failed(ex);
        }
        finally
        {
            progress.Stop();
            await SettleAsync(sendingProgress);
        }
    }

    /// <summary>Sends progress on every stage change and at most every <see cref="ProgressInterval"/>, until done.</summary>
    private static async Task SendProgressAsync(ExportProgressState progress, IServerStreamWriter<ExportResponse> responseStream, CancellationToken cancellationToken)
    {
        ExportProgressSnapshot? sent = null;
        while (true)
        {
            var current = progress.Read();
            if (progress.IsStopped)
            {
                return;
            }

            if (current != sent)
            {
                await responseStream.WriteAsync(new ExportResponse { Progress = GrpcMapping.ToProto(current) }, cancellationToken);
                sent = current;
            }

            if (current.Stage == Core.Exports.ExportStage.Completed)
            {
                return;
            }

            await progress.WaitAsync(ProgressInterval, cancellationToken);
        }
    }

    private static async Task SendFileAsync(Stream content, IServerStreamWriter<ExportResponse> responseStream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken)) > 0)
            {
                await responseStream.WriteAsync(new ExportResponse { Chunk = ByteString.CopyFrom(buffer, 0, read) }, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Waits for the progress loop to end. Its errors (e.g. the caller disconnected) surface through the export.</summary>
    private static async Task SettleAsync(Task sendingProgress)
    {
        try
        {
            await sendingProgress;
        }
        catch (Exception) when (sendingProgress.IsFaulted || sendingProgress.IsCanceled)
        {
        }
    }
}
