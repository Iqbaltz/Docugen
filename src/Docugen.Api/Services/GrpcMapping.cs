using System.Text.Json;
using Docugen.Core.Exports;
using Docugen.Core.Reports;
using Docugen.Infrastructure.Exports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Proto = Docugen.Contracts.V1;

namespace Docugen.Api.Services;

/// <summary>Converts between the Docugen model and the docugen.v1 protobuf messages.</summary>
internal static class GrpcMapping
{
    public static OutputFormat? FromProto(Proto.OutputFormat format) => format switch
    {
        Proto.OutputFormat.Pdf => OutputFormat.Pdf,
        Proto.OutputFormat.Xlsx => OutputFormat.Xlsx,
        Proto.OutputFormat.Csv => OutputFormat.Csv,
        _ => null,
    };

    public static Proto.OutputFormat ToProto(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => Proto.OutputFormat.Pdf,
        OutputFormat.Xlsx => Proto.OutputFormat.Xlsx,
        OutputFormat.Csv => Proto.OutputFormat.Csv,
        _ => Proto.OutputFormat.Unspecified,
    };

    /// <summary>Lower-case name used in messages: pdf, xlsx, csv.</summary>
    public static string Name(OutputFormat format) => format.ToString().ToLowerInvariant();

    /// <summary>Turns the request's Struct into the JSON values <see cref="Core.Parameters.ParameterBinder"/> expects.</summary>
    public static Dictionary<string, JsonElement>? ToJson(Struct? parameters)
    {
        if (parameters is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(JsonFormatter.Default.Format(parameters));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public static Proto.ReportSummary ToSummary(ReportDefinition report)
    {
        var summary = new Proto.ReportSummary { Key = report.Key, Name = report.Name, Description = report.Description ?? "" };
        summary.Formats.AddRange(report.Formats.Select(ToProto));
        return summary;
    }

    public static Proto.Report ToReport(ReportDefinition report)
    {
        var message = new Proto.Report
        {
            Key = report.Key,
            Name = report.Name,
            Description = report.Description ?? "",
            ExactProgress = !string.IsNullOrWhiteSpace(report.CountQuery),
        };
        message.Formats.AddRange(report.Formats.Select(ToProto));
        message.Columns.AddRange(report.Columns.Select(c => new Proto.ReportColumn { Field = c.Field, Header = c.Header ?? c.Field }));

        foreach (var parameter in report.Parameters)
        {
            var info = new Proto.ReportParameter
            {
                Name = parameter.Name,
                Type = JsonSerializer.Serialize(parameter.Type).Trim('"'),
                Required = parameter.Required,
                Label = parameter.Label ?? parameter.Name,
                Description = parameter.Description ?? "",
            };

            if (parameter.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } fallback)
            {
                info.DefaultValue = Value.Parser.ParseJson(fallback.GetRawText());
            }

            if (parameter.AllowedValues is { } allowed)
            {
                info.AllowedValues.AddRange(allowed);
            }

            if (parameter.MaxLength is { } maxLength)
            {
                info.MaxLength = maxLength;
            }

            message.Parameters.Add(info);
        }

        return message;
    }

    public static Proto.ExportProgress ToProto(ExportProgressSnapshot progress)
    {
        var message = new Proto.ExportProgress
        {
            Stage = progress.Stage switch
            {
                ExportStage.Queued => Proto.ExportStage.Queued,
                ExportStage.CountingRows => Proto.ExportStage.CountingRows,
                ExportStage.Querying => Proto.ExportStage.Querying,
                ExportStage.WritingRows => Proto.ExportStage.WritingRows,
                ExportStage.Finalizing => Proto.ExportStage.Finalizing,
                ExportStage.Completed => Proto.ExportStage.Completed,
                _ => Proto.ExportStage.Unspecified,
            },
            Percent = progress.Percent,
            RowsWritten = progress.RowsWritten,
        };

        if (progress.TotalRows is { } total)
        {
            message.TotalRows = total;
        }

        return message;
    }

    public static Proto.ExportFileInfo ToProto(ExportedFile file) => new()
    {
        FileName = file.FileName,
        ContentType = file.ContentType,
        Size = file.Size,
        RowCount = file.RowCount,
        ExportId = file.ExportId.ToString(),
    };
}