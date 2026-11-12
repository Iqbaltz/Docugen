// Runs a Docugen export and saves the file.
//
//   dotnet run --project samples/Docugen.ExportClient -- <report-key> <pdf|xlsx|csv> [Name=Value ...] [options]
//   dotnet run --project samples/Docugen.ExportClient -- sales-orders pdf FromDate=2026-01-01 ToDate=2026-03-31
//
// Options:
//   --server <url>   Docugen address (default http://localhost:5180)
//   --key <key>      API key (default: DOCUGEN_API_KEY, then the first key in the API's user secrets)
//   --out <folder>   where to save the file (default: your Downloads folder)
//
// Parameter values are sent as text, which Docugen converts to the parameter's type; for lists, pass JSON: Ids=[1,2,3].

using System.Diagnostics;
using Docugen.Contracts.V1;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;

var positional = new List<string>();
var parameters = new Struct();
string server = "http://localhost:5180", outFolder = DefaultOutFolder();
string? key = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" when i + 1 < args.Length: server = args[++i]; break;
        case "--key" when i + 1 < args.Length: key = args[++i]; break;
        case "--out" when i + 1 < args.Length: outFolder = args[++i]; break;
        case var arg when arg.Contains('=', StringComparison.Ordinal):
            var (name, value) = (arg[..arg.IndexOf('=', StringComparison.Ordinal)], arg[(arg.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            parameters.Fields[name] = value.StartsWith('[') ? Value.Parser.ParseJson(value) : Value.ForString(value);
            break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count != 2 || ParseFormat(positional[1]) is not { } format)
{
    Console.Error.WriteLine("Usage: Docugen.ExportClient <report-key> <pdf|xlsx|csv> [Name=Value ...] [--server <url>] [--key <key>] [--out <folder>]");
    return 2;
}

key ??= Environment.GetEnvironmentVariable("DOCUGEN_API_KEY") ?? KeyFromUserSecrets();
if (string.IsNullOrEmpty(key))
{
    Console.Error.WriteLine("No API key. Pass --key, set DOCUGEN_API_KEY, or add one with: dotnet user-secrets set \"Docugen:ApiKeys:Postman\" \"<secret>\" --project src/Docugen.Api");
    return 2;
}

using var channel = GrpcChannel.ForAddress(server);
var exports = new ExportService.ExportServiceClient(channel);
var request = new ExportRequest { ReportKey = positional[0], Format = format, Parameters = parameters };

Directory.CreateDirectory(outFolder);
string? path = null;
FileStream? file = null;
var stopwatch = Stopwatch.StartNew();
try
{
    using var call = exports.Export(request, new Metadata { { "x-api-key", key } }, deadline: DateTime.UtcNow.AddMinutes(10));
    await foreach (var message in call.ResponseStream.ReadAllAsync())
    {
        switch (message.PayloadCase)
        {
            case ExportResponse.PayloadOneofCase.Progress:
                var p = message.Progress;
                var total = p.HasTotalRows ? $"/{p.TotalRows:N0}" : "";
                Console.Write($"\r{p.Percent,3}%  {Stage(p.Stage),-22} {p.RowsWritten:N0}{total} rows   ");
                break;
            case ExportResponse.PayloadOneofCase.File:
                Console.WriteLine();
                path = Path.Combine(outFolder, Path.GetFileName(message.File.FileName));
                file = File.Create(path);
                Console.WriteLine($"Saving {message.File.FileName} ({message.File.Size:N0} bytes, {message.File.RowCount:N0} rows)...");
                break;
            case ExportResponse.PayloadOneofCase.Chunk:
                message.Chunk.WriteTo(file!);
                break;
        }
    }

    file?.Dispose();
    Console.WriteLine($"Saved {path} in {stopwatch.Elapsed.TotalSeconds:N1} s.");
    return 0;
}
catch (RpcException ex)
{
    file?.Dispose();
    if (path is not null)
    {
        File.Delete(path); // never leave half a file behind
    }

    Console.WriteLine();
    Console.Error.WriteLine($"Export failed: {ex.StatusCode}: {ex.Status.Detail}");
    var status = ex.GetRpcStatus();
    if (status?.GetDetail<ErrorInfo>() is { } info)
    {
        Console.Error.WriteLine($"Reason: {info.Reason}, export id: {info.Metadata.GetValueOrDefault("export_id")}");
    }

    foreach (var violation in status?.GetDetail<BadRequest>()?.FieldViolations ?? [])
    {
        Console.Error.WriteLine($"  {violation.Field}: {violation.Description}");
    }

    return 1;
}

static OutputFormat? ParseFormat(string value) => value.ToUpperInvariant() switch
{
    "PDF" => OutputFormat.Pdf,
    "XLSX" => OutputFormat.Xlsx,
    "CSV" => OutputFormat.Csv,
    _ => null,
};

static string Stage(ExportStage stage) => stage switch
{
    ExportStage.Queued => "waiting for a slot",
    ExportStage.CountingRows => "counting rows",
    ExportStage.Querying => "running the query",
    ExportStage.WritingRows => "writing rows",
    ExportStage.Finalizing => "completing the file",
    ExportStage.Completed => "done",
    _ => "",
};

static string DefaultOutFolder()
{
    var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    return Directory.Exists(downloads) ? downloads : Directory.GetCurrentDirectory();
}

// The API's user secrets (same UserSecretsId) hold the local keys under Docugen:ApiKeys; use the first one.
static string? KeyFromUserSecrets() =>
    new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build()
        .GetSection("Docugen:ApiKeys").GetChildren().Select(c => c.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v));
